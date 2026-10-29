# 方向评审记录

状态：用户已选择 A，选择原话及依据见 direction-approved.md。深化原型位于 ../prototype/index.html。以下保留第一轮展示记录。

| 方向 | 原型 | 截图 |
|---|---|---|
| A 清晰工作台 | dir-1.html | dir-1.png |
| B 深色工程台 | dir-2.html | dir-2.png |
| C 留白伴行 | dir-3.html | dir-3.png |

六候选总览：thumbs.html、thumbs.png。备选D/E/F只提供缩略初稿。
各方向另有 -plugins.png、-settings.png 和 -1440/-1280/-375.png。

## 验证
统一 Playwright 脚本 verify_prototypes.py，结果 verification.json。
三版均：控制台无页面脚本错误；1440×900、1280×800、375×812无页面横向溢出。
四个审批分支逐一测试后输入解锁，审批按钮不可二次答复；五个dock可切换，终端明确未实现。
命令面板、设置模拟测试、宠物开关、状态展开、侧栏折叠可操作。
三版设计检查无红灯；A/B的等宽标签提示对应真实路径、代码与行号，作为技术内容保留。
外观比较：A/B骨架相关0.28，A/C 0.37，B/C 0.23；工具提示A/C底色色板接近，人工复核两者均为浅色阅读画布，但右栏组织、操作权重与陪伴区域不同。两版仍保留供用户比较，并未把“浅色”本身视作不可使用。

本轮仅验证HTML原型行为，不代表 Avalonia / SDK / IPC 集成完成。
