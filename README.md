# 小工具仓库

日常使用和深度学习研究中的小工具，按目录独立维护。

| 工具 | 功能 | 使用方式 |
| --- | --- | --- |
| [关屏时间切换](tools/screen-timeout-toggle/) | Windows 插电关屏时间在「1 分钟」和「从不」之间切换 | 下载仓库 ZIP 并解压，双击目录中的 `关屏时间切换.exe` |
| [深度学习尺寸计算器](tools/deep-learning-calculator/) | 卷积、转置卷积、池化、感受野计算，支持目标尺寸反推 | 用浏览器打开目录中的 `index.html` |

## 下载和使用

点击 GitHub 页面的 **Code → Download ZIP**，完整解压后进入对应工具目录。也可以克隆：

```bash
git clone https://github.com/AllPass001/small-tools.git
```

关屏工具包含可直接运行的程序、源代码、构建脚本和桌面快捷方式安装脚本；计算器为单文件网页，可离线运行。各工具的详细说明见各自目录中的 README。

## 目录

```text
tools/
├── screen-timeout-toggle/      # Windows 关屏时间切换
└── deep-learning-calculator/   # 深度学习尺寸计算器
```

计算器从 [convolution-shape-calculator](https://github.com/AllPass001/convolution-shape-calculator) 迁入。原有提交历史完整保存在 [Git 历史归档](history/) 中，旧仓库也保留历史和迁移入口。后续更新统一维护在本仓库。
