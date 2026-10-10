import type { LeafAppPlugin } from "@redbamboo/utility"
import { StorageApp } from "./App"

export const plugin: LeafAppPlugin = {
  id: "storage",
  Page: StorageApp,
}
