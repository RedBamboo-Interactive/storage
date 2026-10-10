import assert from "node:assert/strict"
import { readFile } from "node:fs/promises"
import test from "node:test"

const root = new URL("../", import.meta.url)

test("manifest exposes the Storage app and guarded filesystem permission", async () => {
  const manifest = JSON.parse(await readFile(new URL("plugin.json", root), "utf8"))
  assert.equal(manifest.id, "storage")
  assert.equal(manifest.frontend.route, "/apps/storage")
  assert.equal(manifest.permissions[0].target, "redleaf-storage")
})

test("frontend keeps cleanup behind preview and quarantine", async () => {
  const app = await readFile(new URL("web/src/App.tsx", root), "utf8")
  assert.match(app, /cleanup\/preview/)
  assert.match(app, /cleanup\/quarantine/)
  assert.match(app, /Restore remains available/)
  assert.match(app, /Type the quarantine ID/)
})

test("frontend distinguishes logical workspace usage", async () => {
  const app = await readFile(new URL("web/src/App.tsx", root), "utf8")
  assert.match(app, /Logical workspaces/)
  assert.match(app, /already backed by RedLeaf's database/)
})
