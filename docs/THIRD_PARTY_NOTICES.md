# 第三方许可和资源

Windows x64 自包含版含 Microsoft .NET 10.0.15 及 .NET Desktop Runtime。随包保留 Microsoft SDK 分发的原始 `LICENSE.dotnet.txt` 与 `ThirdPartyNotices.dotnet.txt`。

- [.NET Runtime 源码及许可](https://github.com/dotnet/runtime)
- [Windows Forms 源码及许可](https://github.com/dotnet/winforms)
- [WPF 源码及许可](https://github.com/dotnet/wpf)

## 项目自有图片

项目所有者已确认拥有原始软件图标、默认遮罩和配套品牌图片的授权权利，并授权这些资源随源码及运行包公开分发、纳入项目 MIT 许可。对应的 ICO 嵌入 EXE，程序复用该图标显示在窗口和托盘。默认遮罩嵌入程序集。

## 系统接口及开发参考

程序通过 Windows 自带的 UI Automation 与 MSAA 工作，并使用 Windows 系统字体。系统字体文件未随包分发。

早期 QQWindowLab 的开发记录参考了 [QQSafeChat](https://github.com/TheD0ubleC/QQSafeChat)、[qq-operations](https://github.com/treers2/qq-operations) 和 [AstrQQBridge](https://github.com/zhouwu97/AstrQQBridge) 的设计与控件定位。运行包没有打包这些项目的模块。
