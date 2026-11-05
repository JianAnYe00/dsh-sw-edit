import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { copyFile, mkdir, mkdtemp, readFile, rm, rename, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";

export const name = "dsh-sw-edit";
export const inject = ["tools"];

const HERE = dirname(fileURLToPath(import.meta.url));
const DRIVER_SOURCE = join(HERE, "driver", "Program.cs");
const INTEROP_MAIN = "SolidWorks.Interop.sldworks.dll";
const INTEROP_CONST = "SolidWorks.Interop.swconst.dll";

function frameworkDir() {
  const windir = process.env.SystemRoot ?? process.env.windir ?? "C:\\Windows";
  for (const arch of ["Framework64", "Framework"]) {
    const dir = join(windir, "Microsoft.NET", arch, "v4.0.30319");
    if (existsSync(join(dir, "csc.exe"))) return dir;
  }
  throw new Error("csc.exe (.NET Framework 4) was not found.");
}

function cacheRoot() {
  return join(process.env.LOCALAPPDATA ?? process.env.USERPROFILE ?? "C:\\", "dsh-sw-edit");
}

function runProcess(command, args, { timeoutMs } = {}) {
  return new Promise((resolve) => {
    const child = spawn(command, args, { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
    let stdout = "";
    let stderr = "";
    let settled = false;
    let timer;
    const finish = (code, extra) => {
      if (settled) return;
      settled = true;
      if (timer) clearTimeout(timer);
      resolve({ code, stdout, stderr, ...extra });
    };
    child.stdout?.setEncoding("utf8");
    child.stderr?.setEncoding("utf8");
    child.stdout?.on("data", (c) => { stdout += c; });
    child.stderr?.on("data", (c) => { stderr += c; });
    child.on("error", (e) => finish(-1, { spawnError: e }));
    child.on("close", (code) => finish(code ?? -1, {}));
    if (timeoutMs) {
      timer = setTimeout(() => { child.kill(); finish(-2, { timedOut: true }); }, timeoutMs);
      timer.unref?.();
    }
  });
}

async function regValue(key) {
  const run = await runProcess("reg.exe", ["query", key, "/ve"], { timeoutMs: 10000 });
  if (run.code !== 0) return null;
  const match = /REG_SZ\s+(.+?)\s*$/m.exec(run.stdout);
  return match ? match[1].trim() : null;
}

/** Finds the folder holding the SolidWorks interop assemblies (api\redist).
 *  Order: explicit override, the COM registration (works for any install location
 *  and version), then the Program Files conventions. */
async function findInterop() {
  const candidates = [];
  if (process.env.DSH_SW_INTEROP_DIR) candidates.push(process.env.DSH_SW_INTEROP_DIR);

  try {
    const clsid = await regValue("HKCR\\SldWorks.Application\\CLSID");
    if (clsid) {
      const server = await regValue(`HKCR\\CLSID\\${clsid}\\LocalServer32`);
      if (server) candidates.push(join(dirname(server.replace(/^"|"$/g, "")), "api", "redist"));
    }
  } catch {}

  for (const root of [process.env.ProgramFiles, process.env.ProgramW6432, "C:\\Program Files"]) {
    if (!root) continue;
    for (const vendor of ["SOLIDWORKS Corp\\SOLIDWORKS", "SolidWorks Corp\\SolidWorks"]) {
      candidates.push(join(root, vendor, "api", "redist"));
    }
  }

  for (const dir of candidates) {
    if (dir && existsSync(join(dir, INTEROP_MAIN))) return dir;
  }
  throw new Error(
    "SolidWorks interop assemblies (api\\redist) were not found. Install SolidWorks, or set the " +
      "environment variable DSH_SW_INTEROP_DIR to the folder holding SolidWorks.Interop.sldworks.dll.",
  );
}

let building;

async function ensureDriver() {
  if (!building) {
    building = (async () => {
      const cscDir = frameworkDir();
      const interop = await findInterop();
      const source = await readFile(DRIVER_SOURCE, "utf8");
      const hash = createHash("sha256").update(source).update(interop).update(cscDir).digest("hex").slice(0, 12);
      const dir = join(cacheRoot(), `driver-${hash}`);
      const exe = join(dir, "dsh-sw-edit-driver.exe");
      if (existsSync(exe) && existsSync(join(dir, INTEROP_MAIN))) return exe;

      await mkdir(dir, { recursive: true });
      await copyFile(join(interop, INTEROP_MAIN), join(dir, INTEROP_MAIN));
      await copyFile(join(interop, INTEROP_CONST), join(dir, INTEROP_CONST));

      const temp = join(dir, `build-${process.pid}.exe`);
      const result = await runProcess(join(cscDir, "csc.exe"), [
        "/nologo", "/target:exe", "/platform:x64", "/optimize+",
        `/out:${temp}`,
        `/r:${join(interop, INTEROP_MAIN)}`,
        `/r:${join(interop, INTEROP_CONST)}`,
        "/r:System.dll",
        DRIVER_SOURCE,
      ], { timeoutMs: 180000 });

      if (result.code !== 0 || !existsSync(temp)) {
        throw new Error("Compiling the SolidWorks feature helper failed:\n" + (result.stderr || result.stdout || "").trim());
      }
      await rm(exe, { force: true });
      await rename(temp, exe);
      return exe;
    })().catch((error) => { building = undefined; throw error; });
  }
  return building;
}

async function runDriver(lines) {
  const exe = await ensureDriver();
  const dir = await mkdtemp(join(tmpdir(), "swedit-"));
  try {
    const requestPath = join(dir, "request.txt");
    await writeFile(requestPath, lines.join("\r\n"), "utf8");
    const run = await runProcess(exe, [requestPath], { timeoutMs: 240000 });
    const out = (run.stdout || "").trim();
    if (run.code === -2) throw new Error("SolidWorks did not answer in time (a dialog may be open).");
    if (!out) throw new Error((run.stderr || "").trim() || "The SolidWorks helper returned nothing.");
    if (/^ERROR /m.test(out)) throw new Error(out.replace(/^ERROR /gm, ""));
    return out;
  } finally {
    await rm(dir, { recursive: true, force: true }).catch(() => {});
  }
}

export function apply(ctx) {
  const tool = {
    name: "solidworks_feature",
    description:
      "Inspect or edit the feature tree of an open SolidWorks part: list features with their suppressed state, delete named features, suppress or unsuppress them, and save. Deleting a parent feature also deletes the features built on it, so pass only the features you mean to remove. Changes stay in SolidWorks until action=save or action=save_as is used.",
    parameters: {
      type: "object",
      additionalProperties: false,
      properties: {
        action: {
          type: "string",
          enum: ["list", "info", "delete", "suppress", "unsuppress", "save", "save_as"],
          description: "What to do with the feature tree.",
        },
        path: {
          type: "string",
          description: "Absolute path of the SolidWorks part to work on. Omit to use the active document.",
        },
        features: {
          type: "array",
          items: { type: "string" },
          description: "Feature names to delete, suppress or unsuppress.",
        },
        out: {
          type: "string",
          description: "Target path for action=save_as.",
        },
      },
      required: ["action"],
    },
    output: {
      schema: {
        type: "object",
        additionalProperties: false,
        properties: { text: { type: "string" } },
      },
      render: (_args, value) => [{ type: "text", text: value.text }],
    },
    timeoutMs: 300000,
    async execute(args) {
      const lines = [args.action, args.path ?? ""];
      if (args.action === "save_as") lines.push(args.out ?? "");
      else for (const f of args.features ?? []) lines.push(f);
      const text = await runDriver(lines);
      return { text };
    },
  };
  ctx.effect(() => ctx.tools.register(tool), "dsh-sw-edit: tool solidworks_feature");
}
