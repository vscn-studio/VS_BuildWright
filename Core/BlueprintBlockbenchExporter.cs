using Newtonsoft.Json.Linq;

namespace Buildwright.Core;

public enum BlueprintModelFormat { ItemShape, Blockbench, BlenderGlb, PrintStl }

/// <summary>Adapts Shape JSON for the Vintage Story Blockbench plugin, which ignores element scales.</summary>
public static class BlueprintBlockbenchExporter
{
    public static JObject Build(JObject shape)
    {
        var result = (JObject)shape.DeepClone();
        foreach (var element in ((JArray)result["elements"]!).OfType<JObject>()) Bake(element, [1, 1, 1]);
        // The plugin searches these three game texture directories and expects paths without a domain.
        foreach (var texture in ((JObject)result["textures"]!).Properties())
        {
            var path = texture.Value.Value<string>()!;
            var separator = path.IndexOf(':');
            if (separator >= 0 && path[..separator] is "game" or "creative" or "survival")
                texture.Value = path[(separator + 1)..];
        }
        return result;
    }

    private static void Bake(JObject element, double[] inherited)
    {
        // A nonuniform parent scale followed by a rotation can introduce shear, which BB cubes cannot represent.
        string[] axes = ["X", "Y", "Z"];
        for (var axis = 0; axis < 3; axis++)
            if (Math.Abs(element["rotation" + axes[axis]]?.Value<double>() ?? 0) > 1e-10
                && Math.Abs(inherited[(axis + 1) % 3] - inherited[(axis + 2) % 3]) > 1e-10)
                throw new InvalidDataException("Blockbench cannot represent a rotated child under a nonuniform scale: " + element["name"]);

        var scale = axes.Select(axis => element["scale" + axis]?.Value<double>() ?? 1).ToArray();
        var from = element["from"]!.ToObject<double[]>()!;
        var to = element["to"]!.ToObject<double[]>()!;
        var origin = element["rotationOrigin"]?.ToObject<double[]>() ?? [0, 0, 0];
        var bakedFrom = new double[3];
        var bakedTo = new double[3];
        var bakedOrigin = new double[3];
        var childScale = new double[3];
        for (var i = 0; i < 3; i++)
        {
            childScale[i] = inherited[i] * scale[i];
            bakedOrigin[i] = inherited[i] * origin[i];
            bakedFrom[i] = bakedOrigin[i] + childScale[i] * (from[i] - origin[i]);
            bakedTo[i] = bakedFrom[i] + childScale[i] * (to[i] - from[i]);
        }
        element["from"] = new JArray(bakedFrom);
        element["to"] = new JArray(bakedTo);
        // The engine defaults to zero; the plugin defaults to 'from'. Always make the engine pivot explicit.
        element["rotationOrigin"] = new JArray(bakedOrigin);
        foreach (var axis in axes) element.Remove("scale" + axis);
        if (element["children"] is JArray children)
            foreach (var child in children.OfType<JObject>()) Bake(child, childScale);
    }
}
