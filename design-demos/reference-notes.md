# 四个参考项目如何进入 Dougong 原型

用户于本轮补充：“四个参考项目也可以借鉴”。下列是产品设计取舍，功能事实与具体原型的设计解读分开。

| 参考 | 官方来源 | 本轮借鉴 | 具体落点 |
|---|---|---|---|
| dsh / DeepSeek Harness | https://github.com/deepseek-ai/deepseek-harness 及 packages/client/ui-layout/README.md | 用户方案中已确认的三栏、右侧dock、可替换界面模块 | 三版共同外壳，插件页区分UI与Agent扩展 |
| Codex | https://openai.com/index/introducing-the-codex-app/ | 官方描述的按项目组织线程、任务内检查改动 | A中工作区/会话分组、收敛的对话阅读、改动入口 |
| ZCode | https://github.com/zai-org/ZCode 与 https://zcode.z.ai/en | 官方展示的项目会话和工具执行记录；用户方案调研确认的内联审批与状态胶囊 | B中并排的工具证据与文件预览；三版共同保留审批和计时 |
| WorkBuddy | https://cloud.tencent.com.cn/product/workbuddy 与 https://www.workbuddy.cn/ | 官方描述的自然语言任务、规划与交付；三分区是基于用户方案的设计借鉴 | C中计划优先的右栏、工作区范围与对话关联 |

四者均已检索官方来源；参考板 refs/four-products.jpg 留存浏览器截图。参考仅用于设计分析，不在 Dougong 界面放置其他品牌Logo，不暗示合作。
已亲眼查看参考板。初次 OpenAI 发布文章截图为空，未把它当作视觉证据；已改用官方文档 https://developers.openai.com/codex/app/ 成功截图，和 https://deepseek.com/harness 一起存于 refs/codex-dsh.jpg。该文档URL当前展示整合桌面应用及 Codex 工作区截图，不能当作旧版 Codex App 的逐像素还原依据。
WorkBuddy 官网若仅显示营销内容或演示视频，不能据此推断闭源实现。原型不引入其模式切换、市场、并行Agent或远控功能。

Nova / Zed 为界面组织的补充参照；refs/board.jpg 已亲眼查看。Nova截图包含深色工作区与左侧文件树，Zed为文档页面中的工作区布局说明，不假称看到了其完整产品截图。

## 保持 Dougong 自己的特征
真实黑白合构 Logo、榫灵、4档审批、不可关闭的状态胶囊、只读文件工具、仅5个dock页、UI扩展故障隔离。
所有原型运行数据明确为演示；插件装载仅模拟本地目录流程，终端明确未实现。
