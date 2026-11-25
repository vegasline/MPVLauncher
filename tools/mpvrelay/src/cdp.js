// Minimal CDP client over --remote-debugging-pipe.
//
// The browser speaks length-free JSON messages delimited by a NUL byte: commands
// go out on descriptor 3, replies and events come back on descriptor 4. No TCP
// port is ever opened, so nothing on the machine can attach to the session.
export class CdpClient {
  constructor(writable, readable) {
    this.writable = writable;
    this.readable = readable;
    this.buffer = Buffer.alloc(0);
    this.nextId = 1;
    this.pending = new Map();
    this.handlers = new Map();
    this.closed = false;
    this.readable.on("data", (chunk) => this.onData(chunk));
    this.readable.on("error", () => this.dispose());
    this.readable.on("close", () => this.dispose());
    this.onClose = null;
  }

  onData(chunk) {
    this.buffer = Buffer.concat([this.buffer, chunk]);
    let index;
    while ((index = this.buffer.indexOf(0)) >= 0) {
      const raw = this.buffer.subarray(0, index).toString("utf8");
      this.buffer = this.buffer.subarray(index + 1);
      let message;
      try {
        message = JSON.parse(raw);
      } catch {
        continue;
      }
      if (message.id && this.pending.has(message.id)) {
        const entry = this.pending.get(message.id);
        this.pending.delete(message.id);
        if (message.error) {
          entry.reject(new Error(`${entry.method}: ${message.error.message || "error"}`));
        } else {
          entry.resolve(message.result || {});
        }
      } else if (message.method) {
        const handler = this.handlers.get(message.method);
        if (handler) handler(message.params || {}, message.sessionId);
      }
    }
  }

  send(method, params = {}, sessionId = undefined, timeoutMs = 30000) {
    if (this.closed) return Promise.reject(new Error("cdp closed"));
    const id = this.nextId++;
    const message = { id, method, params };
    if (sessionId) message.sessionId = sessionId;
    this.writable.write(JSON.stringify(message) + "\0");
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`${method}: timeout`));
      }, timeoutMs);
      this.pending.set(id, {
        method,
        resolve: (value) => {
          clearTimeout(timer);
          resolve(value);
        },
        reject: (error) => {
          clearTimeout(timer);
          reject(error);
        },
      });
    });
  }

  on(method, handler) {
    this.handlers.set(method, handler);
  }

  dispose() {
    if (this.closed) return;
    this.closed = true;
    for (const entry of this.pending.values()) entry.reject(new Error("cdp closed"));
    this.pending.clear();
    if (this.onClose) this.onClose();
  }

  async waitForReady(attempts = 40, delayMs = 250) {
    let lastError = null;
    for (let i = 0; i < attempts; i++) {
      try {
        return await this.send("Browser.getVersion", {}, undefined, 5000);
      } catch (error) {
        lastError = error;
        await new Promise((resolve) => setTimeout(resolve, delayMs));
      }
    }
    throw lastError || new Error("browser did not answer");
  }
}
