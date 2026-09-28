using System.Text.Json;
using System.Text.Json.Nodes;

namespace Plugins.WorkBuddy;

/// <summary>用于安全检查可选 JSON 节点类型的内部扩展方法。</summary>
internal static class WorkBuddyJsonNodeExtensions
{
	/// <summary>返回 JsonValue 实际承载的 JSON 类型；其他节点返回 Object，空节点返回 Null。</summary>
	public static JsonValueKind GetValueKindSafe(this JsonNode? node)
	{
		if (node is JsonValue jsonValue && jsonValue.TryGetValue<JsonElement>(out var value))
		{
			return value.ValueKind;
		}
		if (node != null)
		{
			return JsonValueKind.Object;
		}
		return JsonValueKind.Null;
	}
}
