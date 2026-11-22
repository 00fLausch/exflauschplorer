import { cp, mkdir, readFile } from 'node:fs/promises'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const packageDir = path.join(web, 'node_modules', 'pdfjs-dist')
const { version } = JSON.parse(await readFile(path.join(packageDir, 'package.json'), 'utf8'))
const target = path.join(web, 'public', 'pdfjs', version)
await mkdir(target, { recursive: true })
await cp(path.join(packageDir, 'LICENSE'), path.join(target, 'LICENSE.txt'))
for (const name of ['cmaps', 'standard_fonts', 'wasm', 'iccs']) {
  await cp(path.join(packageDir, name), path.join(target, name), { recursive: true, force: true })
}
