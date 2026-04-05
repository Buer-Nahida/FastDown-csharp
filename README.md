# Fast Down (C#)

这是一个***纯*** AI 项目！`fd-cs` 文件夹内有 AI 写的文档及全部代码，此 README.md 仅为开源 AI 提示词而创建

## 初始化

- 从 [Fast Down](https://github.com/fast-down/ffi) 组织下克隆 `addons` `cli` `core` `ffi` `updater` 这几个项目，除此之外文件夹下无任何内容
- 我使用的是 `gemini-cli` AI 工具，使用了 `/dir add .` 并使用 `/model` 命令切换到了 `gemini-3-pro-preview` 模型
- 安装 `git`

### 提示词 （第一段）

```text
当前所在的文件夹下有 5 个文件夹，这 5 个文件夹是 GitHub 项目 `fast-down` 的 cli 的自身依赖项，现在请你用 C# 重构这些依赖项以及 cli 本身，以 fd-cs 文件夹名保存整个项目在当前工作目录下，确保**重构后的项目可以编译、功能完全正常**，编译方法写到该文件夹的 README.md 里，**一定**要读取全部文件后再进行编程以确保你对项目的了解充足，正常编写时请勤用搜索功能，每次编写代码前先通过搜索文档、语法等说明确保代码无低级错误，若遇到缺失的依赖项你务必使用搜索功能去自行找寻并使用 `git clone` 命令克隆下来自行读取、理解，https://github.com/fast-down/ 这个是原始项目的组织账号，你可以在这下面找寻我没有克隆的 fast-down 构成组件，找到后记得克隆下来按照前面的过程处理
```

### 提示词（第二段）

这是在第一次执行完后我继续给 AI 的提示词

```text
请使用 nix shell nixpkgs#<package0> nixpkgs#<package1> -c <command> 获取缺失的二进制并执行命令，以检查项目的代码和运行状况，并使用 https://drive.nahida.im/d/gdrive/UserUpload/%E3%80%90angely%E3%80%91%E5%B0%8F%E8%8D%89%E7%A5%9E%20NAHIDA%20HD%20VIDEO.mp4 这个我为你准备的 1.72G 文件测试软件的下载是否正常，若上述情况有任一条件不达标请重新检查你的代码并修改并重复这个步骤直到达成目标
```

### 附加说明

- 你可以将两段提示词合并在一起使用，但不保证出什么问题
- 如果你的电脑系统不是 NixOS 你可能需要更改下第二段提示词

## 协议与所有权

***我自愿将此作品的所有权交给 Fast Down 的作者 [share121](https://github.com/share121)，并且开源协议定为 WTFPL，你可以使用此项目干任何事，但任何人都不会为你的所作所为造成的任何损失负责***
