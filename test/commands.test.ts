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

test("parseBridgeCommand parses channel-default set with an alias or path", () => {
  assert.deepEqual(parseBridgeCommand("channel-default repo"), {
    kind: "channel-default",
    action: "set",
    rawPath: "repo",
  });
  assert.deepEqual(parseBridgeCommand("channel-default C:/repo/project"), {
    kind: "channel-default",
    action: "set",
    rawPath: "C:/repo/project",
  });
});

test("parseBridgeCommand parses channel-default clear", () => {
  assert.deepEqual(parseBridgeCommand("channel-default clear"), {
    kind: "channel-default",
    action: "clear",
  });
  assert.deepEqual(parseBridgeCommand("CHANNEL-DEFAULT Clear"), {
    kind: "channel-default",
    action: "clear",
  });
});

test("parseBridgeCommand parses bare channel-default as show", () => {
  assert.deepEqual(parseBridgeCommand("channel-default"), {
    kind: "channel-default",
    action: "show",
  });
  assert.deepEqual(parseBridgeCommand("  channel-default   "), {
    kind: "channel-default",
    action: "show",
  });
});

