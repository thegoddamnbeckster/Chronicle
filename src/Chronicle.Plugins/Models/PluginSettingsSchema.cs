namespace Chronicle.Plugins.Models;

/// <summary>Describes all configurable settings exposed by a plugin.</summary>
public class PluginSettingsSchema
{
    public List<SettingDefinition> Settings { get; set; } = [];
}

/// <summary>A single configurable setting declared by a plugin.</summary>
public class SettingDefinition
{
    /// <summary>Unique key used to store/retrieve the value.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Human-readable label shown in the UI.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Optional help text shown beneath the field.</summary>
    public string? Description { get; set; }

    public SettingType Type { get; set; } = SettingType.Text;

    public bool Required { get; set; }

    /// <summary>Default value serialised as string. Null means no default.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Allowed options for Dropdown and MultiSelect types.</summary>
    public List<SelectOption> Options { get; set; } = [];
}

public class SelectOption
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

public enum SettingType
{
    Text,
    Password,
    Number,
    Boolean,
    Dropdown,
    MultiSelect,
    Url,
    FilePath,
    TextArea
}
