# AutoPickup 架构（冻结决策）
## 决策
- C# .NET 8 自包含单文件 exe (win-x64)；WinForms 向导+状态+托盘
- 音频 cue：进程音频会话峰值表（IAudioMeterInformation），无需 VBCABLE（可选 CABLE 兼容）
- 视觉 v1：UI 面板定位 + 多尺度模板 + Windows OCR + 迟滞投票
- 范围 v1：稳定切换 + 到达验证 + 自检向导 + 原版等效取货循环
## 安全边界
允许：GDI/DXGI 客户区抓屏、ViGEmBus 虚拟手柄(SendInput 降级)、WASAPI 会话音量表、netsh 防火墙、窗口消息。
禁止：读内存/注入/Hook/自定义驱动（ViGEmBus 除外）。
## 业务腿序列（等效原版迂回，参数可调）
A 确保故事 → B 离线等计时 → C 关防火墙+进邀请战局 → D 等进场音频 →
E 开防火墙+清断连弹窗 → F 等货到 → G 末轮关窗云同步/回故事；任何失败先还原安全态。
## 目录
Ui/ 向导与主控 | Core/Vision 识别 | Core/Capture 抓帧 | Core/Input 手柄 |
Core/Audio 音量表 | Core/Net 防火墙 | Core/Fsm 状态机 | Core/Jobs 编排 | Config
