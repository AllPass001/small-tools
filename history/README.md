# 计算器原仓库历史

`convolution-shape-calculator.bundle` 保存迁移前计算器仓库的完整 Git 历史，包括原始提交、作者及文件内容。

原仓库：https://github.com/AllPass001/convolution-shape-calculator

归档截止提交：`136f0b63d2f57b140cea0a1606b91ffd39eeedc7`。

验证归档（在任意 Git 仓库中执行）：

```bash
git bundle verify convolution-shape-calculator.bundle
```

恢复成独立仓库：

```bash
git clone -b calculator-history convolution-shape-calculator.bundle calculator-history
```

新仓库保留自己的初始化提交；计算器原提交可通过此归档完整恢复。
