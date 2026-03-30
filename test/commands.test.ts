import test from "node:test";
import assert from "node:assert/strict";
import { parseBridgeCommand } from "../src/core/commands.js";

test("parseBridgeCommand parses restart without rebuild", () => {
  assert.deepEqual(parseBridgeCommand("restart"), {
    kind: "restart-bridge",
    rebuild: false,
  });
  assert.deepEqual(parseBridgeCommand("restart bridge"), {
    kind: "restart-bridge",
    rebuild: false,
  });
  assert.deepEqual(parseBridgeCommand("bridge restart"), {
    kind: "restart-bridge",
    rebuild: false,
  });
});

test("parseBridgeCommand parses restart with rebuild", () => {
  assert.deepEqual(parseBridgeCommand("restart rebuild"), {
    kind: "restart-bridge",
    rebuild: true,
  });
  assert.deepEqual(parseBridgeCommand("restart build"), {
    kind: "restart-bridge",
    rebuild: true,
  });
  assert.deepEqual(parseBridgeCommand("restart bridge rebuild"), {
    kind: "restart-bridge",
    rebuild: true,
  });
});

