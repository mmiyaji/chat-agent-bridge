import path from "node:path";

export function assertPathAllowed(targetPath: string, allowedRoots: string[]): string {
  const resolvedTarget = path.resolve(targetPath);

  for (const root of allowedRoots) {
    const resolvedRoot = path.resolve(root);
    const relative = path.relative(resolvedRoot, resolvedTarget);
    const isInsideRoot =
      relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));

    if (isInsideRoot) {
      return resolvedTarget;
    }
  }

  throw new Error(`Working directory is outside allowed roots: ${resolvedTarget}`);
}
