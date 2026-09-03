namespace ConnectorControl.Core;

/// <summary>Which editor view a connector was last saved from (Swift <c>EditView</c>).</summary>
public enum EditView
{
    Form,
    Json,
}

internal static class EditViewJson
{
    public static string ToJsonString(this EditView view) => view == EditView.Form ? "form" : "json";

    public static bool TryParse(string raw, out EditView view)
    {
        switch (raw)
        {
            case "form": view = EditView.Form; return true;
            case "json": view = EditView.Json; return true;
            default: view = EditView.Form; return false;
        }
    }
}
