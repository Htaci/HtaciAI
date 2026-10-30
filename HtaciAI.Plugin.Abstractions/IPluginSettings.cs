using System;
using Avalonia.Controls;

namespace HtaciAI.Plugin.Abstractions;

/// <summary>
/// 插件在<b>插件页</b>里提供一页自己的设置界面（需要声明
/// <see cref="PluginCapabilities.UiSettings"/>，并让插件类实现本接口）。
///
/// <b>放这里的理由</b>：插件页是用户管理插件的唯一固定入口，「清缓存 / 重置数据 / 开关」
/// 这类操作应该和「启用 / 停用 / 版本号」在一起。让插件为设置另开一个标签页的代价是
/// 入口会飘：标签页被关掉、被切走后，用户就得重新找一遍。
///
/// <b>只做一页，只放轻量内容</b>：
/// <list type="bullet">
///   <item>界面在宿主的一个可滚动区域里，空间有限。<b>不要内嵌 WebView 等独立窗口类控件</b>
///         —— 它们在滚动容器里会被裁切，并盖住页面上其他内容（Airspace）；</item>
///   <item>不做多级导航（点进卡片再跳一层）。条目多的管理界面（登录站点、历史、下载）
///         仍应放在插件自己的标签页里，这里只留入口按钮与概览。</item>
/// </list>
///
/// <b>调用约定</b>：
/// <list type="bullet">
///   <item><see cref="CreateSettingsView"/> 在 <b>UI 线程</b>上调用，<b>会被反复调用</b>：
///         每次进这个分页都会重新 ask。不要在返回的控件里假定自己能活多久；</item>
///   <item>必须能重复创建 —— 不要在方法里 <c>throw new InvalidOperationException("已经创建过")</c>，
///         那会让用户第二次点进来就看不到设置；</item>
///   <item>抛异常不会坏掉整个插件页：宿主会把这一页换成一行「设置界面显示失败」+ 原因；</item>
///   <item>插件没在运行时宿主不会调用它，这一页显示「插件未运行」。</item>
/// </list>
///
/// <b>不要在这里做危险动作的确认弹窗</b>：插件页本身已经是一个独立界面，
/// 用「点一次变红、再点一次执行」的两段式按钮即可，弹窗在插件页里会显得突兀。
/// </summary>
public interface IPluginSettingsProvider
{
    /// <summary>构建设置界面。<b>在 UI 线程上调用，每次进分页都会调用一次。</b></summary>
    Control CreateSettingsView();
}
