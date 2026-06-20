# ThinCJKFont — Book of Hours 中文字体微调

![cover](assets/cover.png)

把《司辰之书》(Book of Hours) 内置简体中文调细:正文变细、粗体不臃肿、斜体改暗色不倾斜。不替换字形、零缺字。参数都在 `config.json`,存盘即时生效,无需重启。

## 安装

1. 订阅本 mod。
2. 同时订阅并启用 **Ghirbi, the Gatekeeper(守门人吉尔比)** —— 游戏靠它放行代码型 mod。
3. 重启游戏,在「设置 → 第六史 / MODS」确认两者已启用。

## 配置

编辑 mod 目录(游戏内「设置 → 浏览文件」)下的 `config.json`:

```json
{
  "configVersion": 2,
  "enabled": true,

  "normalWeight": 0.25,
  "boldWeight": 0.85,
  "thickness": 0,

  "isDisableItalic": true,
  "italicColor": "#FFE300"
}
```

写了某个键就按该值生效,删掉则保持游戏原值;`enabled: false` 恢复原版。完整键说明见同目录 `config.help.html`。

## 构建

按本机路径改 `src/ThinCJKFont.csproj` 的 `<GameManaged>`,然后:

```sh
cd src && dotnet build -c Release
```

产物 `bin/Release/ThinCJKFont.dll` 复制到 mod 目录的 `dll/` 下。

## 目录

```
mod/   即装即用的 mod(创意工坊上传的就是它)
src/   C# 源码与工程文件
assets/  README 封面图
```

作者 XHXIAIEIN。仅调整渲染,不分发任何字体文件。
