# dsh-sw-edit

> Feature-tree editing for SolidWorks inside **DeepSeek Harness** — list, suppress, delete features, and save.
> 给 **DeepSeek Harness（DSH）** 补上 SolidWorks **特征树编辑**能力的插件。

<p align="left">
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D4">
  <img alt="solidworks" src="https://img.shields.io/badge/SOLIDWORKS-2014%2B-E32222">
  <img alt="license" src="https://img.shields.io/badge/license-MIT-3DA639">
  <img alt="dsh" src="https://img.shields.io/badge/DeepSeek%20Harness-plugin-4B6EF5">
</p>

[中文](#中文) · [English](#english)

---

## 中文

### 这是什么

DSH 现有的大多数 SolidWorks 插件（例如 [`@pan-318/dsh-solidworks`](https://github.com/pan-318/dsh-solidworks)）做的是**建模 / 装配 / 工程图 / 导出**，它们都没有"特征树编辑"这个能力：不能压缩特征，更不能删特征。

`dsh-sw-edit` 只做这一件事，用来处理别人交付的零件：清理历史遗留特征、去掉多余几何、把零件整理成可复用的模板件、批量压缩重建。

一个工具，七个动作：

| action | 作用 |
|---|---|
| `list` | 列出全部特征：序号、名称、类型、是否压缩 |
| `info` | 读取体积、质量、实体数 |
| `suppress` | 压缩指定特征（可多个） |
| `unsuppress` | 取消压缩 |
| `delete` | 删除指定特征 |
| `save` | 保存当前文档 |
| `save_as` | 另存为 |

> 非官方项目，与 Dassault Systèmes 无任何关联，也未获其背书。
> 插件通过**你本机已授权安装的** SolidWorks COM 接口工作，不修改 SolidWorks 安装、不绕过任何许可机制。

---

### 环境要求

| 项目 | 要求 |
|---|---|
| 系统 | Windows 10 / 11 x64 |
| SolidWorks | 2014 或更新，`SldWorks.Application` COM 已注册（正常安装即可） |
| .NET Framework | 4.x（Windows 自带；插件用它调用 `csc.exe` 编译驱动） |
| DSH | 桌面版，支持安装 profile 插件 |

---

### 安装

**方式 A：从 GitHub 安装（推荐给外部用户）**

```powershell
dsh plugin --profile desktop add github:JianAnYe00/dsh-sw-edit
```

**方式 B：从本地目录安装**

把整个 `dsh-sw-edit` 目录放到任意位置，在它的**上一级目录**里执行：

```powershell
dsh plugin --profile desktop add "file:.\dsh-sw-edit"
```

**方式 C：从 tarball 安装**

```powershell
dsh plugin --profile desktop add "file:.\dsh-sw-edit-1.0.0.tgz"
```

如果 `dsh` 不在 PATH 里，用 DSH 安装目录下的：

```
<DSH 安装目录>\resources\runtime\cli\bin\dsh.cmd
```

装完**新开一个会话**（或在插件页刷新），就能看到 `solidworks_feature` 工具。

---

### 用法

参数：

- `path`：零件绝对路径，省略则用当前活动文档
- `features`：特征名数组（`suppress` / `unsuppress` / `delete` 用），**必须用 `list` 拿到的准确名称**
- `out`：`save_as` 的目标路径

典型流程：

```
1. list                    → 拿到准确的特征名
2. suppress 若干特征        → 截图 / 看体积，确认效果
3. delete 确认过的特征      → 真正删除
4. save_as 新文件名         → 另存，不覆盖原件
```

示例：先压缩一段特征做试验，确认后再删除（`path` 需要绝对路径，下面是示意）

```text
solidworks_feature { action: "suppress", path: "C:\\Work\\part.SLDPRT", features: ["草图38", "切除-拉伸7"] }
solidworks_feature { action: "info",     path: "C:\\Work\\part.SLDPRT" }   // 看体积/实体数变化
solidworks_feature { action: "delete",   path: "C:\\Work\\part.SLDPRT", features: ["草图38", "切除-拉伸7"] }
solidworks_feature { action: "save_as",  path: "C:\\Work\\part.SLDPRT", out: "C:\\Work\\part-master.SLDPRT" }
```

---

### 工作原理

```
index.js（插件，运行在 DSH 宿主进程里）
   │
   ├─ 首次调用：用 csc.exe 把 driver/Program.cs 编译成独立 exe
   │            互操作程序集路径由 HKCR\SldWorks.Application 注册表项自动定位
   │            编译结果按「源码 + 互操作路径」哈希缓存在 %LOCALAPPDATA%\dsh-sw-edit
   │
   └─ 每次调用：写一个请求文件 → 启动 driver.exe → 读回 stdout
                 driver.exe 通过 SolidWorks COM 接口执行操作
```

**为什么要把驱动编译成独立进程、而不是直接用命令行脚本？**
因为 DSH 的沙箱会隔离工具进程：从 shell 里启动的任何程序都看不到、也连不上宿主侧的 SolidWorks（跨进程 COM 会直接 `MK_E_UNAVAILABLE`）。
插件代码运行在宿主进程里，由它启动的驱动才拥有正常的 COM 访问权限。这个设计对任何想写"宿主级"SolidWorks 自动化的人都适用。

---

### 已知限制与坑

1. **删除父特征会连带删除子特征，且无法撤销。** 动手前先 `save_as` 一份副本。
2. **压缩父特征会连带压缩子特征；但取消压缩父特征不会恢复子特征。**
   反复做压缩试验时，每次试完应"关闭不保存 → 重新打开"，否则模型状态会越试越乱。
3. 第一次调用需要编译驱动，约 5～10 秒；之后命中缓存。
4. SolidWorks 若弹出模态对话框（许可、缺失引用等），本次调用会超时。
5. **曲面体不是实体**：如果零件主体是曲面（`Solid bodies: 0`），`delete` 会把它一并丢掉，而且导出 STL 会因为没有实体而失败。这不是插件的问题，是模型本身的结构问题。
6. 只支持零件（`.SLDPRT`）的特征树；装配体和工程图请用别的工具。

---

### 排错

| 现象 | 处理 |
|---|---|
| `interop assemblies were not found` | 设环境变量 `DSH_SW_INTEROP_DIR` 指向 SolidWorks 的 `api\redist` 目录 |
| `SolidWorks did not answer in time` | 有对话框挡住，或 SolidWorks 未启动；先手动打开 SolidWorks |
| 工具没出现 | 确认 profile 名（桌面版一般是 `desktop`），重启会话试试 |
| 改了 `driver/Program.cs` 但没生效 | 缓存按源码哈希计算；确认文件内容确实变了（同内容替换不会重建） |
| 想清缓存 | 删除 `%LOCALAPPDATA%\dsh-sw-edit` |

---

### 卸载

```powershell
dsh plugin --profile desktop remove dsh-sw-edit
```

再删除 `%LOCALAPPDATA%\dsh-sw-edit`。

---

### 目录结构

```
dsh-sw-edit/
├── package.json          # DSH bundle 声明
├── cordis.patch.yml      # 插件注册补丁
├── index.js              # 工具定义 + 驱动编译/调用
├── driver/Program.cs     # 真正干活的 SolidWorks 驱动（C#）
├── README.md
└── LICENSE
```

---

## English

### What it is

Most SolidWorks plugins for DeepSeek Harness — including [`@pan-318/dsh-solidworks`](https://github.com/pan-318/dsh-solidworks) — cover modelling, assemblies, drawings and export, but none of them can edit the **feature tree**: no suppressing, no deleting.

`dsh-sw-edit` adds exactly that one capability, for cleaning up parts someone else built: removing leftover features, deleting stray geometry, producing a clean master part.

One tool, seven actions: `list`, `info`, `suppress`, `unsuppress`, `delete`, `save`, `save_as`.

### Requirements

- Windows 10/11 x64
- SOLIDWORKS 2014 or newer, with the `SldWorks.Application` COM server registered
- .NET Framework 4.x (ships with Windows; used to compile the driver with `csc.exe`)
- DeepSeek Harness desktop, with profile plugin support

### Install

```powershell
dsh plugin --profile desktop add github:JianAnYe00/dsh-sw-edit
```

or from a local folder / tarball (run it in the parent folder of `dsh-sw-edit`):

```powershell
dsh plugin --profile desktop add "file:.\dsh-sw-edit"
```

Then start a new DSH session; the `solidworks_feature` tool appears.

### How it works

The plugin registers the tool inside the DSH host process. On first use it compiles `driver/Program.cs` into a small exe with `csc.exe`, locating the SolidWorks interop assemblies through the `HKCR\SldWorks.Application` registration (so it works for any install path or version). Each call writes a request file, runs the driver, and returns its stdout.

The driver must be a separate executable launched **by the host**, because the DSH sandbox isolates shell processes: anything started from a shell cannot see or reach a host-side SolidWorks over COM (`MK_E_UNAVAILABLE`).

### Caveats

- Deleting a parent feature also deletes everything built on it, irreversibly.
- Suppressing a parent suppresses its children; unsuppressing the parent does **not** restore them.
- Surface bodies are not solids: deleting them can make STL export fail with "no solid body".
- Parts only (`.SLDPRT` feature trees).

### License

MIT — see [LICENSE](LICENSE).

---

## 致谢 / Acknowledgements

这个插件能写出来，是因为站在别人的肩膀上，特此致谢：

- **[`@pan-318/dsh-solidworks`](https://github.com/pan-318/dsh-solidworks)**（作者 **pan-318**，MIT License）
  本插件的整体形态直接源自这个项目：bundle 的 `package.json` + `cordis.patch.yml` 结构、`index.js` 里 `ctx.tools.register()` 的注册方式、"首次调用时编译 C# 驱动 + 按哈希缓存"的运行时思路，以及 `driver/Program.cs` 中连接 SolidWorks、`Process.Start` 隐藏启动、COM 重试循环等关键实现细节，都是读它的源码学来的。没有这个项目，我根本不知道 DSH 插件该怎么写。
  如果你只需要"建模 / 装配 / 工程图 / 导出"，**建议直接用它**，它的功能比这个插件完整得多。

- **[DeepSeek Harness](https://deepseek.com)** —— 提供了插件机制、工具注册 API 和附件/沙箱基础设施。

- **SolidWorks API 文档与社区**（`SldWorks.chm`、各类 COM 自动化示例）—— `IFeature.Select2` / `IModelDoc2.EditDelete` / `EditSuppress2` 等调用方式均来自公开文档。

- 以及最初提出需求的那位工程师：把一份继承来的、带着多余特征的零件整理成可复用的模板件。这个插件的每一个功能，都是被真实任务逼出来的。

---

## 许可 / License

[MIT](LICENSE)

> SOLIDWORKS 是 Dassault Systèmes SolidWorks Corporation 的注册商标。
> 本项目是非官方社区项目，与 Dassault Systèmes 无任何关联，也未获其背书。
> 使用前请确认你的 SolidWorks 许可允许此类自动化操作。
