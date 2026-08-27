namespace PQA.Web.Models;

public sealed record FieldDefinition(
    string Name,
    string Label,
    string InputType = "text",
    bool Required = false,
    int MaxLength = 100,
    string? Lookup = null,
    bool IsDateTime = false,
    IReadOnlyList<string>? Options = null);

public sealed record WorkflowDefinition(
    string Id,
    string Name,
    string Description,
    string Accent,
    string MasterTable,
    string? AttachmentTable,
    string? AttachmentFolder,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<string> SearchFields,
    string? DetailTable = null,
    IReadOnlyList<FieldDefinition>? DetailFields = null,
    bool IsLineBased = false,
    string? CompletionField = null)
{
    public const string KeyColumn = "登錄案號";
}

public static class WorkflowDefinitions
{
    private static FieldDefinition Text(string name, int length = 100, bool required = false, string? lookup = null) =>
        new(name, name, "text", required, length, lookup);
    private static FieldDefinition DateText(string name) => new(name, name, "rocdate", false, 20);
    private static FieldDefinition CalendarText(string name) => new(name, name, "date", false, 20);
    private static FieldDefinition DateTime(string name) => new(name, name, "date", false, 30, null, true);
    private static FieldDefinition Check(string name) => new(name, name, "checkbox", false, 1);
    private static FieldDefinition Area(string name, int length) => new(name, name, "textarea", false, length);
    private static FieldDefinition Select(string name, params string[] options) =>
        new(name, name, "select", false, options.Max(x => x.Length), null, false, options);

    public static readonly IReadOnlyList<WorkflowDefinition> All = new[]
    {
        new WorkflowDefinition(
            "coordination", "各部門會辦", "跨部門案件登錄、執行與進度追蹤", "#17786f",
            "各部門會辦處理登件表", "各部門會辦處理附件", "各部門會辦",
            new FieldDefinition[]
            {
                CalendarText("收文日期"), Text("會辦單號", 20), Text("來文單位", 20, lookup: "locations"),
                CalendarText("預定完成日期"), Text("執行人員1", 20, lookup: "employees"),
                Text("執行人員2", 20, lookup: "employees"), Text("執行人員3", 20, lookup: "employees"),
                CalendarText("完成日期"), Check("會辦中"), Check("送審中"), Check("跟催中"), Check("取消"),
                Text("總工時", 10), Area("會辦主題", 100)
            },
            new[] { "登錄案號", "會辦單號", "來文單位", "預定完成日期", "完成日期", "會辦主題" },
            CompletionField: "完成日期"),

        new WorkflowDefinition(
            "complaint", "抱怨單", "客戶抱怨、原因查對、改善措施與跟催", "#b75635",
            "抱怨單處理登件表", null, null,
            new FieldDefinition[]
            {
                DateTime("收文日期"), Text("會辦單號", 20), Text("來文單位", 20, lookup: "locations"),
                DateTime("預定完成日期"), Text("執行人員", 20, lookup: "employees"), DateTime("完成日期"),
                Text("會辦處理", 10), Text("總工時", 5), Area("會辦主題", 100), Text("回饋天數", 4)
            },
            new[] { "登錄案號", "會辦單號", "來文單位", "預定完成日期", "完成日期", "會辦主題" },
            "抱怨單詳細內容",
            new FieldDefinition[]
            {
                Text("回饋天數", 4), Text("泰廠抱怨單號", 100), Text("不良品名稱", 100),
                DateTime("不良品出貨日期"), DateTime("客戶開單日期"), Text("國貿會辦單號", 100),
                Area("反應內容", 1800), Area("原因查對", 1800), Area("不良批處理意見", 1800), Area("改進措施", 1800),
                Text("品管回覆文單號", 100), DateTime("回覆文日期"), Text("會辦單號", 100),
                Text("會辦日期", 20), Text("會辦單位", 100), DateTime("第1次跟催"), DateTime("第2次跟催"),
                DateTime("第3次跟催"), DateTime("第4次跟催"), DateTime("第5次跟催"), DateTime("第6次跟催")
            },
            CompletionField: "完成日期"),

        new WorkflowDefinition(
            "development", "開發試製案", "計畫案、物料入庫、會審與發行進度", "#48669b",
            "開發試製案處理登件表", "開發試製案處理附件", "開發試製案",
            new FieldDefinition[]
            {
                CalendarText("收文日期"), Text("計畫案號", 20), Text("物料編號", 11), DateText("預定入庫日期"),
                DateText("實際入庫日期"), DateText("提出會審日期"), Text("執行組別", 20, lookup: "employees"),
                Text("工時", 6), DateText("發行日期"), Area("備註", 100)
            },
            new[] { "登錄案號", "計畫案號", "物料編號", "收文日期", "預定入庫日期", "發行日期" },
            IsLineBased: true,
            CompletionField: "發行日期"),

        new WorkflowDefinition(
            "change", "變更通知單", "變更案件、預定完成項目與執行狀況", "#72559b",
            "變更通知單處理登件表", "變更通知單處理附件", "變更通知單",
            new FieldDefinition[]
            {
                CalendarText("收文日期"), Text("變更單號", 20), Select("預定完成項目", "樣品檢驗表", "標準檢驗表", "符合性查對"), CalendarText("預定完成日期"),
                Text("執行人員1", 20, lookup: "employees"), Text("執行人員2", 20, lookup: "employees"),
                Text("執行人員3", 20, lookup: "employees"), CalendarText("完成日期"), Area("會辦主題", 100), Check("取消")
            },
            new[] { "登錄案號", "變更單號", "預定完成項目", "預定完成日期", "完成日期", "會辦主題" },
            CompletionField: "完成日期")
    };

    public static WorkflowDefinition Get(string id) =>
        All.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException("無效的功能模組。");
}
