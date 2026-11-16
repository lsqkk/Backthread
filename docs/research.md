# 回线 Backthread：调研与设计取舍

调研日期：2026-11-16。范围：Windows 的窗口切换与任务恢复、剪贴板溯源、打开/另存为对话框。以下对比来自产品官方文档、作者发布的项目资料与论文；没有穷尽商店、私有软件或未公开项目，因此不主张「全球首创」。

## 真实场景

你在编辑一篇报告，临时切到聊天看一个问题；为了回复又翻了浏览器、资源管理器和另一份文档。几分钟后，`Alt+Tab` 还记得最后看过哪个窗口，却不知道这串跳转从哪开始。报告可能还开着，光标也在原来的位置；真正丢失的是回到那个窗口的线索。下次若预先知道自己要去处理插单，还会想替未来的自己留一句「回来看第三段的统计口径」。

微软研究院的 [Iqbal 与 Horvitz，CHI 2007](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/11/CHI_2007_Iqbal_Horvitz-1.pdf) 在自然办公环境中追踪了 27 名参与者两周。他们发现，用户在响应提醒之后常会继续访问多个应用；在该研究样本中，提醒导致的切换平均持续近 10 分钟，此后依中断类型平均还要 10 至 15 分钟才重新专注于被打断的任务。被遮住的窗口比较难找，而参与者会特意让原窗口保持打开，利用光标、选区等线索续上工作。[Czerwinski 等人的任务切换日记研究](https://research.microsoft.com/en-us/um/people/horvitz/taskdiary.pdf) 也把任务恢复列为需要界面支持的问题。这些研究说明场景由来已久，不能据此预测回线能替今天的用户省多少时间。

## 已有方案与空位

| 方案 | 已经能做什么 | 回线选的切入点 |
| --- | --- | --- |
| [Windows `Alt+Tab`、任务视图与虚拟桌面](https://support.microsoft.com/en-us/windows/experience/how-to-multitask-in-windows) | 在开着的任务之间切换，或预先把工作分到不同桌面 | 一段可回看的切换路径，保留同一窗口被多次访问的顺序，也显示每段停留 |
| [PowerToys Workspaces](https://learn.microsoft.com/en-us/windows/powertoys/workspaces) | 捕获工作区，一键启动应用并摆放窗口，可使用已有窗口 | 回到一个仍开着的原窗口，保留应用自己的当前状态；无需事先布置工作区 |
| [Windows Recall](https://support.microsoft.com/en-us/windows/ai/ai-features/retrace-your-steps-with-recall) | 用户选择启用后保存屏幕快照，通过快照搜索和回看 | 仅凭窗口切换轨迹提供返回线索，无须读屏、OCR 或保存屏幕内容 |
| [ManicTime](https://docs.manictime.com/win-client/overview) | 记录活动窗口，以时间轴查看应用活动 | 停留时间服务于当下的「回哪个窗口」；不以计时、统计或生成报告为目标 |
| [NoFocusSteal](https://boazcohenj.github.io/NoFocusSteal/) | 记录焦点变化，检测到非自愿焦点抢占时尝试立即归还焦点 | 回线也覆盖自愿打开聊天、网页等形成的多步岔路，由用户决定何时返回 |
| [Harken](https://github.com/grantith/harken) | 通过快捷键在堆叠窗口之间前后切换 | 回线的栈保存的是用户为一次岔路留下的返回点；它与窗口空间布局无关 |
| [Anchor](https://www.reddit.com/r/windowsapps/comments/1u7garl/anchor_a_free_windows_app_that_helps_me_resume/) 与 [Where Was I?](https://www.reddit.com/r/software/comments/1rosiak/i_built_a_free_windows_tray_app_that_saves_your/)（作者发布） | 在中断前留下思路或给当前应用留便签，稍后查看 | 没来得及留便签也可以回看轨迹；主动留点时可以嵌套，按顺序回到原窗口 |
| [SWISH](https://www.microsoft.com/en-us/research/publication/swish-semantic-analysis-of-window-titles-and-switching-history/)（研究原型） | 用窗口标题和切换历史推断哪些窗口属于同一任务 | 以可解释的停留信号提供单个候选，不给任务做语义分类 |

另外两条路很快出现了成熟的直接先例：剪贴板溯源的 [HO ClipTrace](https://github.com/HOStudioApps/HO-ClipTrace) 已能记住来源并尝试返回原处；而 [Direct Folders](https://support.codesector.com/en/articles/10157883-direct-folders-features) 已能让 Windows 打开/保存对话框跳到资源管理器已开的目录，还支持为不同应用设默认目录。复制历史、快速跳目录都很实用，但并非这里要从头再做的方向。

在上述范围内，没有找到同时把**短期切换轨迹、按停留提议一个原窗口、可嵌套的人工返回点**做成同一个恢复操作的产品。新意在这个组合：无预警中断用轨迹，预知插单时可以像浏览器的返回键一样留点，遇到插单里又插单就逐层退回。

## 为什么做成 Windows 应用

回线用 Windows 的桌面事件感知本机前台窗口变化，按具体窗口区分两个同名或同应用的实例，并在用户明确要求返回时请求操作系统把原窗口带到前台。浏览器页面和移动应用无法在 Windows 用户会话里普遍接收其他本机应用的窗口事件、验证原窗口还存活、恢复最小化状态并激活它。WinUI 提供窗口、文本与键盘交互，桌面集成依靠 Win32。参见 [WinEvents](https://learn.microsoft.com/en-us/windows/win32/winauto/winevents-overview)、[SetForegroundWindow 的系统限制](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow) 和 [RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)。

- **先给提示，再由人做决定。** 停得久只是一个线索，例如灵机一动切到网页读了很久，也可能改变候选。轨迹要让候选看得懂，并允许直接指定其它窗口。
- **返回点留给自己的下一步。** 提示可以只是一个记号，也可以加一句简短笔记。有两层岔路时，应先回到内层的窗口，再回到外层的工作。
- **回到原来的窗口。** 原窗口关闭后，应用不会启动一个看起来相同的窗口冒充原窗口。它也不负责复原已关闭的文档、浏览器标签页或未保存数据。
- **短路径比全天档案更贴合场景。** 默认只在最近 15 分钟的范围内找停留至少 45 秒的候选。轨迹和返回点仅驻留本次运行的内存，不保存截图、网页正文、键入内容或剪贴板。窗口标题本身仍可能含有文档名或聊天对象，不能当作无敏感信息处理。

验证价值的办法是用自己常开的三个窗口，对比同样的两次岔路：一次只用 `Alt+Tab` 找回，另一次用回线。记录从「想起来要回去」到「原窗口已聚焦」的用时、找错几次、候选是否有帮助。重复一周后，看真正愿意使用的是自动候选、手动返回点，还是仅在偶发中断时打开轨迹；暂不把未经测量的提速写进产品承诺。
