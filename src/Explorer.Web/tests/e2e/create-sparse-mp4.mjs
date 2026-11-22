import { closeSync, ftruncateSync, openSync, readFileSync, writeSync } from 'node:fs'
import { execFileSync } from 'node:child_process'

const GOP_COUNT = 2400
const TARGET_SIZE = 5_000_000_000n

export function createSparseH264Mp4(sourcePath, destinationPath) {
  const source = readFileSync(sourcePath)
  const topLevel = parseBoxes(source, 0, source.length)
  const ftyp = topLevel.find((candidate) => candidate.type === 'ftyp')
  const moov = topLevel.find((candidate) => candidate.type === 'moov')
  if (!ftyp || !moov) throw new Error('The H.264 source fixture has no MP4 file type or movie box.')

  const trak = childBox(source, moov, 'trak')
  const mdia = childBox(source, trak, 'mdia')
  const minf = childBox(source, mdia, 'minf')
  const stbl = childBox(source, minf, 'stbl')
  const stsd = childBox(source, stbl, 'stsd')
  const stsz = childBox(source, stbl, 'stsz')
  const stsc = childBox(source, stbl, 'stsc')
  const stts = childBox(source, stbl, 'stts')
  const stss = findChild(source, stbl, 'stss')
  const stco = findChild(source, stbl, 'stco') ?? childBox(source, stbl, 'co64')
  if (findChild(source, stbl, 'ctts')) throw new Error('The H.264 source fixture must not have composition-time offsets.')

  const entryCount = source.readUInt32BE(stsd.payloadStart + 4)
  if (entryCount < 1) throw new Error('The H.264 source fixture has no sample description.')
  const sampleEntryStart = stsd.payloadStart + 8
  const sampleEntrySize = source.readUInt32BE(sampleEntryStart)
  const sampleEntryEnd = sampleEntryStart + sampleEntrySize
  if (sampleEntrySize < 16 || sampleEntryEnd > stsd.end) throw new Error('The H.264 sample description is invalid.')
  const sampleEntry = source.subarray(sampleEntryStart, sampleEntryEnd)
  if (sampleEntry.toString('ascii', 4, 8) !== 'avc1') throw new Error('The H.264 source fixture does not use an AVC1 sample entry.')

  const originalSampleCount = source.readUInt32BE(stsz.payloadStart + 8)
  const uniformSampleSize = source.readUInt32BE(stsz.payloadStart + 4)
  if (originalSampleCount < 1) throw new Error('The H.264 source fixture has no video samples.')
  const originalSampleSizes = Array.from({ length: originalSampleCount }, (_, index) =>
    uniformSampleSize || source.readUInt32BE(stsz.payloadStart + 12 + index * 4))
  if (originalSampleSizes.some((size) => size < 1)) throw new Error('The H.264 source fixture contains an empty sample.')
  const originalSamples = readSourceSamples(source, stsc, stco, originalSampleSizes)
  const timingEntries = readTimingEntries(source, stts)
  const sampleDelta = timingEntries[0]?.sampleDelta
  const mediaTimescale = source.readUInt32BE(childBox(source, mdia, 'mdhd').payloadStart + 12)
  if (!sampleDelta || !mediaTimescale || timingEntries.some((entry) => entry.sampleDelta !== sampleDelta) ||
    timingEntries.reduce((total, entry) => total + entry.sampleCount, 0) !== originalSampleCount)
    throw new Error('The H.264 source fixture must use a uniform, complete sample timeline.')

  const sourceSyncSamples = stss
    ? new Set(readSyncSamples(source, stss))
    : new Set(Array.from({ length: originalSampleCount }, (_, index) => index + 1))
  const frameCount = originalSampleCount * GOP_COUNT
  const sizes = Array.from({ length: frameCount }, (_, index) => originalSampleSizes[index % originalSampleCount])
  const syncSamples = Array.from({ length: GOP_COUNT }, (_, gopIndex) =>
    [...sourceSyncSamples].map((sampleNumber) => gopIndex * originalSampleCount + sampleNumber)).flat()
  const mediaDuration = frameCount * sampleDelta
  const movieDuration = Math.round(mediaDuration * 1000 / mediaTimescale)
  const width = sampleEntry.readUInt16BE(32)
  const height = sampleEntry.readUInt16BE(34)
  const movieHeader = fullBox('mvhd', 0, Buffer.concat([
    Buffer.alloc(8), uint32(1000), uint32(movieDuration), uint32(0x00010000), uint16(0x0100), uint16(0),
    Buffer.alloc(8), identityMatrix(), Buffer.alloc(24), uint32(2),
  ]))
  const trackHeader = fullBox('tkhd', 7, Buffer.concat([
    Buffer.alloc(8), uint32(1), uint32(0), uint32(movieDuration), Buffer.alloc(8),
    uint16(0), uint16(0), uint16(0), uint16(0), identityMatrix(), uint32(width << 16), uint32(height << 16),
  ]))
  const mediaHeader = fullBox('mdhd', 0, Buffer.concat([
    Buffer.alloc(8), uint32(mediaTimescale), uint32(mediaDuration), uint16(0x55c4), uint16(0),
  ]))
  const handler = fullBox('hdlr', 0, Buffer.concat([
    uint32(0), Buffer.from('vide', 'ascii'), Buffer.alloc(12), Buffer.from('VideoHandler\0', 'ascii'),
  ]))
  const videoMediaHeader = fullBox('vmhd', 1, Buffer.concat([uint16(0), Buffer.alloc(6)]))
  const selfContainedUrl = fullBox('url ', 1, Buffer.alloc(0))
  const dataReference = fullBox('dref', 0, Buffer.concat([uint32(1), selfContainedUrl]))
  const dataInformation = box('dinf', dataReference)
  const sampleDescription = fullBox('stsd', 0, Buffer.concat([uint32(1), sampleEntry]))
  const timeToSample = fullBox('stts', 0, Buffer.concat([uint32(1), uint32(frameCount), uint32(sampleDelta)]))
  const sampleToChunk = fullBox('stsc', 0, Buffer.concat([uint32(1), uint32(1), uint32(1), uint32(1)]))
  const sampleSizeTable = fullBox('stsz', 0, Buffer.concat([uint32(0), uint32(frameCount), ...sizes.map(uint32)]))
  const syncSampleTable = fullBox('stss', 0, Buffer.concat([uint32(syncSamples.length), ...syncSamples.map(uint32)]))

  const makeMovieBox = (offsets) => {
    const chunkOffsets = fullBox('co64', 0, Buffer.concat([uint32(offsets.length), ...offsets.map(uint64)]))
    const sampleTable = box('stbl', Buffer.concat([sampleDescription, timeToSample, syncSampleTable, sampleToChunk, sampleSizeTable, chunkOffsets]))
    const mediaInformation = box('minf', Buffer.concat([videoMediaHeader, dataInformation, sampleTable]))
    const media = box('mdia', Buffer.concat([mediaHeader, handler, mediaInformation]))
    const track = box('trak', Buffer.concat([trackHeader, media]))
    return box('moov', Buffer.concat([movieHeader, track]))
  }

  const provisionalMovie = makeMovieBox(Array(frameCount).fill(0n))
  const mediaDataOffset = BigInt(ftyp.size + provisionalMovie.length + 16)
  const mediaLength = TARGET_SIZE - mediaDataOffset
  const groupData = Buffer.concat(originalSamples)
  const totalSampleBytes = BigInt(groupData.length) * BigInt(GOP_COUNT)
  const gap = (mediaLength - totalSampleBytes) / BigInt(GOP_COUNT - 1)
  if (gap <= BigInt(groupData.length)) throw new Error('The sparse video has insufficient space between indexed GOPs.')
  const offsets = Array(frameCount).fill(0n)
  for (let gopIndex = 0; gopIndex < GOP_COUNT; gopIndex++) {
    let sampleOffset = mediaDataOffset + BigInt(gopIndex) * gap
    for (let sampleIndex = 0; sampleIndex < originalSampleCount; sampleIndex++) {
      offsets[gopIndex * originalSampleCount + sampleIndex] = sampleOffset
      sampleOffset += BigInt(originalSampleSizes[sampleIndex])
    }
  }
  if (offsets.at(-1) + BigInt(sizes.at(-1)) > TARGET_SIZE) throw new Error('The final H.264 sample lies beyond the sparse video file.')
  const finalMovie = makeMovieBox(offsets)
  if (finalMovie.length !== provisionalMovie.length) throw new Error('The MP4 offset table changed the movie-header size.')

  const mdatSize = TARGET_SIZE - BigInt(ftyp.size + finalMovie.length)
  const mdatHeader = Buffer.concat([uint32(1), Buffer.from('mdat', 'ascii'), uint64(mdatSize)])
  const descriptor = openSync(destinationPath, 'wx')
  closeSync(descriptor)
  const fsutil = `${process.env.WINDIR ?? 'C:\\Windows'}\\System32\\fsutil.exe`
  execFileSync(fsutil, ['sparse', 'setflag', destinationPath], { windowsHide: true, stdio: 'pipe' })

  const output = openSync(destinationPath, 'r+')
  try {
    ftruncateSync(output, Number(TARGET_SIZE))
    writeSync(output, ftyp.buffer, 0, ftyp.size, 0)
    writeSync(output, finalMovie, 0, finalMovie.length, ftyp.size)
    writeSync(output, mdatHeader, 0, mdatHeader.length, ftyp.size + finalMovie.length)
    for (let gopIndex = 0; gopIndex < GOP_COUNT; gopIndex++)
      writeSync(output, groupData, 0, groupData.length, Number(mediaDataOffset + BigInt(gopIndex) * gap))
  } finally { closeSync(output) }

  return {
    path: destinationPath,
    size: Number(TARGET_SIZE),
    durationSeconds: mediaDuration / mediaTimescale,
    frameCount,
    gopCount: GOP_COUNT,
    sourceSampleCount: originalSampleCount,
    syncSampleCount: syncSamples.length,
    totalSampleBytes: Number(totalSampleBytes),
  }
}

function readSourceSamples(source, stsc, chunkOffsetBox, sampleSizes) {
  const mappingCount = source.readUInt32BE(stsc.payloadStart + 4)
  const mappings = Array.from({ length: mappingCount }, (_, index) => {
    const offset = stsc.payloadStart + 8 + index * 12
    return {
      firstChunk: source.readUInt32BE(offset),
      samplesPerChunk: source.readUInt32BE(offset + 4),
    }
  })
  if (mappings.length === 0 || mappings[0].firstChunk !== 1 || mappings.some((mapping) => mapping.samplesPerChunk < 1))
    throw new Error('The H.264 source fixture has an invalid sample-to-chunk table.')

  const chunkCount = source.readUInt32BE(chunkOffsetBox.payloadStart + 4)
  const samples = []
  let sampleIndex = 0
  let mappingIndex = 0
  for (let chunkIndex = 1; chunkIndex <= chunkCount; chunkIndex++) {
    while (mappingIndex + 1 < mappings.length && mappings[mappingIndex + 1].firstChunk <= chunkIndex) mappingIndex++
    const offsetIndex = chunkOffsetBox.payloadStart + 8 + (chunkIndex - 1) * (chunkOffsetBox.type === 'co64' ? 8 : 4)
    const chunkOffset = chunkOffsetBox.type === 'co64'
      ? Number(source.readBigUInt64BE(offsetIndex))
      : source.readUInt32BE(offsetIndex)
    if (!Number.isSafeInteger(chunkOffset)) throw new Error('The H.264 chunk offset cannot be represented safely.')
    let sampleOffset = chunkOffset
    for (let chunkSample = 0; chunkSample < mappings[mappingIndex].samplesPerChunk && sampleIndex < sampleSizes.length; chunkSample++) {
      const size = sampleSizes[sampleIndex]
      if (sampleOffset + size > source.length) throw new Error('An H.264 access unit lies outside the source fixture.')
      samples.push(source.subarray(sampleOffset, sampleOffset + size))
      sampleOffset += size
      sampleIndex++
    }
  }
  if (sampleIndex !== sampleSizes.length) throw new Error('The H.264 chunk table does not address every video sample.')
  return samples
}

function readTimingEntries(source, stts) {
  const entryCount = source.readUInt32BE(stts.payloadStart + 4)
  return Array.from({ length: entryCount }, (_, index) => {
    const offset = stts.payloadStart + 8 + index * 8
    return { sampleCount: source.readUInt32BE(offset), sampleDelta: source.readUInt32BE(offset + 4) }
  })
}

function readSyncSamples(source, stss) {
  const entryCount = source.readUInt32BE(stss.payloadStart + 4)
  return Array.from({ length: entryCount }, (_, index) => source.readUInt32BE(stss.payloadStart + 8 + index * 4))
}

function identityMatrix() {
  return Buffer.concat([
    uint32(0x00010000), uint32(0), uint32(0),
    uint32(0), uint32(0x00010000), uint32(0),
    uint32(0), uint32(0), uint32(0x40000000),
  ])
}

function fullBox(type, flags, payload) {
  const header = Buffer.alloc(4)
  header.writeUIntBE(flags, 1, 3)
  return box(type, Buffer.concat([header, payload]))
}

function box(type, payload) {
  const output = Buffer.alloc(8 + payload.length)
  output.writeUInt32BE(output.length, 0)
  output.write(type, 4, 4, 'ascii')
  payload.copy(output, 8)
  return output
}

function parseBoxes(buffer, start, end) {
  const boxes = []
  for (let offset = start; offset + 8 <= end;) {
    const size32 = buffer.readUInt32BE(offset)
    const type = buffer.toString('ascii', offset + 4, offset + 8)
    const headerSize = size32 === 1 ? 16 : 8
    const size = size32 === 1 ? Number(buffer.readBigUInt64BE(offset + 8)) : size32 === 0 ? end - offset : size32
    if (!Number.isSafeInteger(size) || size < headerSize || offset + size > end)
      throw new Error(`The source MP4 has an invalid ${type} box.`)
    boxes.push({ type, start: offset, size, payloadStart: offset + headerSize, end: offset + size, buffer })
    offset += size
  }
  return boxes
}

function childBox(buffer, parent, type) {
  const found = findChild(buffer, parent, type)
  if (!found) throw new Error(`The source MP4 has no ${type} box.`)
  return found
}

function findChild(buffer, parent, type) {
  return parseBoxes(buffer, parent.payloadStart, parent.end).find((candidate) => candidate.type === type)
}

function uint16(value) {
  const output = Buffer.alloc(2)
  output.writeUInt16BE(value)
  return output
}

function uint32(value) {
  const output = Buffer.alloc(4)
  output.writeUInt32BE(value)
  return output
}

function uint64(value) {
  const output = Buffer.alloc(8)
  output.writeBigUInt64BE(BigInt(value))
  return output
}
