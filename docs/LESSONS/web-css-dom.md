# Web CSS/DOM 行为 失败教训（2026-08-18）

> **TL;DR**：覆盖层进场动画（`translateY(+12px)` 上浮）让元素短暂伸出容器底缘，同时输入框 `focus()` 自动聚焦触发浏览器的 scroll-into-view，把 `overflow: hidden` 的祖先容器**程序化滚动**了 ~12px——表现为"动画期间游戏画面被顶起、动画结束恢复"。`overflow: hidden` 只禁用户滚动、不禁程序滚动；真正"截断且不可滚"的是 `overflow: clip`。

## 教训清单

| # | 标题 | 一句话 |
|---|---|---|
| 1 | `overflow: hidden` 仍可被程序滚动 | hidden 只禁止用户滚动条/手势；`focus()` 的 scroll-into-view、`scrollIntoView()`、`scrollTop` 赋值照样滚动它（本例 `.app-main`/`.app-shell` 都是 hidden 仍被滚 12px） |
| 2 | `focus()` 默认滚动所有可滚动祖先 | 不传 `{ preventScroll: true }` 时浏览器把焦点元素滚进**每一层**可滚动祖先的可视区，包括 `overflow: hidden` 的容器 |
| 3 | transform 动画溢出会临时撑大 scrollHeight | `translateY(+12px)` 起始帧使覆盖层伸出底缘 → 祖先 `scrollHeight > clientHeight` 12px → focus 有处可滚 → 动画结束溢出消失、scrollTop 被钳回 0，画面"弹回" |
| 4 | `position: absolute` 只防布局挤压不防程序滚动 | "不参与文档流所以不会顶开内容"的推理只排除 layout 一条路径；scroll-into-view 引起的**滚动位移**仍会让文档流内容移动 |
| 5 | 截断溢出用 `overflow: clip` 而非 hidden | clip 不创建 scroll container、溢出不向祖先传播，程序滚动也无处可滚（Chrome 90+）；hidden 换个容器继续被顶 |
| 6 | "fixed 元素不动、文档流元素动" = 祖先被滚了 | 排查画面位移时先问"哪些元素在动"：fixed 定位元素纹丝不动而普通内容上移，说明不是布局变化而是某层祖先 scrollTop 变了 |

---

## 1. 覆盖层进场动画 + 自动聚焦 → 游戏输出被顶起 12px

**场景**：InputBar（终端底部 absolute 覆盖层）增加淡入 + 上浮进场动画（`inputbar-enter-from { opacity: 0; transform: translateY(12px) }`）。动画期间游戏输出整体被向上顶 ~12px，动画结束（180ms）恢复原位。按钮（`position: fixed`）不受影响；游戏输出很短、末尾行离底部很远时仍被顶起。

**结果**：动画期间画面抖动，进场完成后"弹回"。与内容高度、虚拟滚动 sticky 状态完全无关。

**原因**：三因素叠加，缺一不可：

1. **进场起始帧溢出**：输入栏静止位 `bottom: 0`，动画起点 `translateY(+12px)` 使其底边伸出 `.terminal-view` 底缘 ~12px，侵入祖先 overflow 区域，`scrollHeight` 比 `clientHeight` 大 12px。
2. **focus 触发程序滚动**：进入 WaitInput 时输入框自动 `focus()`（未传 `preventScroll`），浏览器把焦点元素滚进所有可滚动祖先的可视区——`overflow: hidden` 的 `.app-main`/`.app-shell` 也被滚了 12px（hidden 只禁用户滚动，禁不住程序滚动）。此刻输入框恰在视口底缘下方 12px，于是整页被顶起。
3. **动画结束钳回**：transition 结束 transform 移除，溢出消失，`scrollHeight` 收缩回 `clientHeight`，浏览器把 scrollTop 钳回 0——画面"恢复正常"。

**解决**：`.terminal-view` 加 `overflow: clip`。clip 不创建滚动容器、被裁溢出不向祖先传播，focus 的 scroll-into-view 无处可滚；动画起始帧被裁的 12px 处于 `opacity: 0` 状态，无视觉影响。保持"从下方 12px 浮上来"的视觉方向不变。

**备选方案**（各有取舍）：
- `focus({ preventScroll: true })`：掐断触发器，但其他程序滚动路径（`scrollIntoView` 等）不设防；
- enter-from 改为向上偏移（`translateY(-12px)`）：元素不超底缘，但视觉方向变为"从上方降下来"，与整套浮层动效语言不一致。

**教训**：覆盖层动画的位移方向若指向容器外，必须同时考虑溢出裁剪与程序滚动的交互；"absolute 不占布局"挡不住 scroll-into-view。

## 2. 排查手法：scrollTop 时间线 + 元素分类

**场景**：画面"被顶一下又弹回"，时长仅 ~180ms，肉眼难定位。

**手法**：
- DevTools 在动画瞬间读可疑容器 `scrollTop`：`.app-main.scrollTop` 呈 0 → ~12 → 0，直接锁定被滚的容器；
- 按"哪些元素在动"分类：fixed 元素（⌨/⋮ 悬浮按钮）不动、文档流内容（游戏输出）上移 → 排除布局挤压，锁定祖先滚动；
- AnyKey/EnterKey 回合（无 input、无 focus）不复现 → 锁定 focus 是触发器。

**教训**：瞬时位移类 bug 先区分「布局变化」与「滚动位移」两个世界——判据是 fixed/absolute 与文档流元素是否**分道扬镳**；再用 scrollTop 读数确认滚的是哪一层。
