export interface JobQueue {
  enqueue<T>(sessionId: string, job: () => Promise<T>): Promise<T>;
}

export class InMemoryJobQueue implements JobQueue {
  private readonly tails = new Map<string, Promise<unknown>>();

  async enqueue<T>(sessionId: string, job: () => Promise<T>): Promise<T> {
    const previous = this.tails.get(sessionId) ?? Promise.resolve();
    let release: (() => void) | undefined;

    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });

    const nextTail = previous
      .catch(() => undefined)
      .then(() => gate);

    this.tails.set(sessionId, nextTail);

    try {
      await previous.catch(() => undefined);
      return await job();
    } finally {
      release?.();
      if (this.tails.get(sessionId) === nextTail) {
        this.tails.delete(sessionId);
      }
    }
  }
}
