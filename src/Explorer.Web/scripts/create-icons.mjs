import { readFile, mkdir } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'

const root = fileURLToPath(new URL('../', import.meta.url))
const publicDir = path.join(root, 'public')
const svg = await readFile(path.join(publicDir, 'icon-source.svg'))
await mkdir(publicDir, { recursive: true })
await sharp(svg).resize(192, 192).png().toFile(path.join(publicDir, 'pwa-192.png'))
await sharp(svg).resize(512, 512).png().toFile(path.join(publicDir, 'pwa-512.png'))
await sharp(svg).resize(180, 180).png().toFile(path.join(publicDir, 'apple-touch-icon.png'))
