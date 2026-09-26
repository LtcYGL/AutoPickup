using System.ComponentModel;
using System.Reflection;

namespace AutoPickup.Config;

/// <summary>「3 音频 · 捕获设备」的下拉候选：实时枚举当前活动的输出设备（回环可捕获的目标）。
/// 不做成“只能选” —— 仍允许手打设备名片段（模糊匹配逻辑不变）。</summary>
public sealed class CaptureDeviceConverter : StringConverter
{
    public const string Default = "(系统默认)";

    public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context)
    {
        var items = new List<string> { Default };
        try
        {
            foreach (var (idx, name, isDef) in AutoPickup.Core.Audio.NAudioCueSource.ListDevices())
                items.Add("#" + idx + " " + name + (isDef ? "  [默认]" : ""));
        }
        catch { }
        return new StandardValuesCollection(items);
    }
}

/// <summary>参数页用的“扁平视图”：把 AppSettings 各 Section 的所有叶子属性摊平成可直接编辑的行，
/// 每行沿用原属性上的 Category/DisplayName/Description，PropertyGrid 原生按中文分类显示。
///
/// 关键点（2026-09-12 修复参数值编辑后回跳）：PropertyGrid 在提交编辑、刷新、重新选中对象时，
/// 会对 ICustomTypeDescriptor 调用 <see cref="ICustomTypeDescriptor.GetPropertyOwner(PropertyDescriptor)"/>，
/// 再用返回的对象做 GetValue/SetValue 的 owner；若返回 null，WinForms 内部会对 null 调 GetType() 抛
/// NullReferenceException（表现为“重设 SelectedObject 崩溃 / 值不写回被回退”）。因此这里统一返回
/// 真正的叶子宿主 Section 实例，并保证 GetClassName/GetComponentName/GetDefaultProperty 都非 null。</summary>
public sealed class SettingsView : ICustomTypeDescriptor
{
    private readonly PropertyDescriptorCollection _props;
    private readonly object _root;

    public SettingsView(AppSettings settings)
    {
        _root = settings;
        var list = new List<PropertyDescriptor>();
        var seen = new HashSet<string>();
        int order = 0;
        foreach (var secProp in typeof(AppSettings).GetProperties())
        {
            if (secProp.GetIndexParameters().Length > 0) continue;
            var sec = secProp.GetValue(settings);
            if (sec is null) continue;
            foreach (var p in sec.GetType().GetProperties())
            {
                if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
                string name = p.Name;
                if (!seen.Add(name)) name = secProp.Name + "." + p.Name;
                string cat = p.GetCustomAttribute<CategoryAttribute>()?.Category ?? "其它";
                string disp = p.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? p.Name;
                string desc = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                list.Add(new LeafProperty(sec, p, name, cat, disp, desc, order++));
            }
        }
        // 排序：先按分类的数字前缀（原来直接用序数比，两位数分类会排到一位数前面），
        // 同一分类内按**声明顺序**（原来按 DisplayName 序数比，中文等于按 Unicode 码位，组内是乱的）。
        list.Sort((a, b) =>
        {
            int c = CategoryOrder(a.Category).CompareTo(CategoryOrder(b.Category));
            if (c == 0) c = string.CompareOrdinal(a.Category, b.Category);
            if (c == 0) c = ((LeafProperty)a).Order.CompareTo(((LeafProperty)b).Order);
            return c;
        });
        _props = new PropertyDescriptorCollection(list.ToArray());
    }

    /// <summary>分类的数字前缀（如 "12 xxx"=12）；没有数字前缀的排到最后。</summary>
    private static int CategoryOrder(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        return i > 0 && int.TryParse(s.Substring(0, i), out int n) ? n : int.MaxValue;
    }

    public AttributeCollection GetAttributes()
        => new(new Attribute[]
        {
            new CategoryAttribute("参数"),
            new DescriptionAttribute("全部可调参数：直接点值列修改，改完点[保存参数]写盘（即时作用于后续动作）"),
        });

    public string GetClassName() => "AutoPickup 参数";
    public string GetComponentName() => "参数";
    public TypeConverter GetConverter() => new ExpandableObjectConverter();
    public EventDescriptor? GetDefaultEvent() => null;
    public PropertyDescriptor? GetDefaultProperty() => _props.Count > 0 ? _props[0] : null;
    public object? GetEditor(Type editorBaseType) => null;
    public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
    public EventDescriptorCollection GetEvents(Attribute[]? attributes) => EventDescriptorCollection.Empty;
    public PropertyDescriptorCollection GetProperties() => _props;
    public PropertyDescriptorCollection GetProperties(Attribute[]? attributes) => _props;

    /// <summary>PropertyGrid 取叶子属性的读写宿主：返回该行真正所属的 Section 实例（不能返回 null）。</summary>
    public object GetPropertyOwner(PropertyDescriptor? pd)
        => pd is LeafProperty leaf ? leaf.Target : _root;

    /// <summary>编辑后校验：按显示名从真实配置对象回读该行的实际值（确保写回的不是界面缓存）。</summary>
    public bool TryReadBack(string displayName, out object? value)
    {
        foreach (PropertyDescriptor pd in _props)
        {
            if (string.Equals(pd.DisplayName, displayName, StringComparison.Ordinal)
                || string.Equals(pd.Name, displayName, StringComparison.Ordinal))
            {
                value = pd is LeafProperty leaf ? leaf.ReadDirect() : null;
                return true;
            }
        }
        value = null;
        return false;
    }

    /// <summary>非参数化重载（PropertyGrid 重设选中对象时会用它）同样返回宿主，避免对 null 调 GetType()。</summary>
    object? ICustomTypeDescriptor.GetPropertyOwner(PropertyDescriptor? pd) => GetPropertyOwner(pd);

    private sealed class LeafProperty : PropertyDescriptor
    {
        private readonly object _target;
        private readonly PropertyInfo _info;
        private readonly string _category;
        private readonly string _display;

        /// <summary>基础特性 = 原属性上的全部特性（保住 [TypeConverter] 等，否则下拉/编辑器根本传不到 PropertyGrid）
        /// + 参数页用的三个显示特性。</summary>
        private static Attribute[] BuildAttrs(PropertyInfo info, string category, string display, string description)
        {
            var attrs = new List<Attribute>();
            foreach (var a in info.GetCustomAttributes(true)) if (a is Attribute at) attrs.Add(at);
            attrs.Add(new CategoryAttribute(category));
            attrs.Add(new DisplayNameAttribute(display));
            attrs.Add(new DescriptionAttribute(description));
            return attrs.ToArray();
        }

        public LeafProperty(object target, PropertyInfo info, string name, string category, string display, string description, int order)
            : base(name, BuildAttrs(info, category, display, description))
        {
            _target = target;
            _info = info;
            _category = category;
            _display = display;
            Order = order;
        }

        public object Target => _target;
        /// <summary>声明顺序（排序用，保证参数页按作者书写顺序展示）。</summary>
        public int Order { get; }

        /// <summary>绕过界面缓存，直接从配置对象读当前值。</summary>
        public object? ReadDirect() => _info.GetValue(_target);
        public override string Category => _category;
        public override string DisplayName => _display;
        public override Type ComponentType => _target.GetType();
        public override bool IsReadOnly => !_info.CanWrite;
        public override Type PropertyType => _info.PropertyType;
        public override bool CanResetValue(object component) => false;
        public override object? GetValue(object? component) => _info.GetValue(_target);
        public override void ResetValue(object component) { }
        public override void SetValue(object? component, object? value) => _info.SetValue(_target, value);
        public override bool ShouldSerializeValue(object component) => false;
    }
}