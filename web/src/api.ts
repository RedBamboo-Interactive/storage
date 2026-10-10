async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    credentials: "include",
    headers: body === undefined ? undefined : { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (response.status === 401) {
    window.location.href = "/login"
    throw new Error("Authentication required")
  }
  if (!response.ok) {
    const detail = await response.json().catch(() => ({ error: response.statusText })) as { error?: string }
    throw new Error(detail.error || response.statusText)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export const api = {
  get: <T>(path: string) => request<T>("GET", path),
  post: <T>(path: string, body?: unknown) => request<T>("POST", path, body),
}
