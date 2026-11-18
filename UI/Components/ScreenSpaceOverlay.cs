using Godot;

namespace Animanager.UI.Components;

/// <summary>Preview annotations retain screen thickness while their coordinates remain in 3D.</summary>
internal static class ScreenSpaceOverlay
{
    public const float LinePixels = 2;
    public const float MarkerRadiusPixels = 3.5f;
    private static readonly Shader[] LineShaders = [Make(false, false), Make(false, true)];
    private static readonly Shader[] MarkerShaders = [Make(true, false), Make(true, true)];
    public static ShaderMaterial Lines(bool xray) => new() { Shader = LineShaders[xray ? 1 : 0] };
    public static ShaderMaterial Marker(Color color, float size, bool xray)
    {
        var material = new ShaderMaterial { Shader = MarkerShaders[xray ? 1 : 0] };
        material.SetShaderParameter("tint", color); material.SetShaderParameter("radius_pixels", MarkerRadiusPixels * size);
        return material;
    }
    public static void SetXray(ShaderMaterial material, bool xray) =>
        material.Shader = (material.Shader == LineShaders[0] || material.Shader == LineShaders[1] ? LineShaders : MarkerShaders)[xray ? 1 : 0];
    private static Shader Make(bool marker, bool xray) => new()
    {
        Code = "shader_type spatial;\nrender_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled"
            + (xray ? ", depth_test_disabled" : "") + ";\n" + (marker ? MarkerCode : LineCode)
    };
    // UV + UV2.x contain the opposite endpoint; UV2.y chooses the ribbon edge.
    private const string LineCode = """
        void vertex() {
            vec3 a = (MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
            vec3 b = (MODELVIEW_MATRIX * vec4(UV, UV2.x, 1.0)).xyz;
            float near_z = -0.011;
            if (a.z > near_z && b.z > near_z) {
                POSITION = vec4(0.0, 0.0, 2.0, 1.0);
            } else {
                if (a.z > near_z) a = mix(a, b, (near_z - a.z) / (b.z - a.z));
                if (b.z > near_z) b = mix(b, a, (near_z - b.z) / (a.z - b.z));
                vec4 p = PROJECTION_MATRIX * vec4(a, 1.0);
                vec4 q = PROJECTION_MATRIX * vec4(b, 1.0);
                vec2 direction = (q.xy / q.w - p.xy / p.w) * VIEWPORT_SIZE;
                float length_px = length(direction);
                vec2 side = length_px > 0.001 ? vec2(-direction.y, direction.x) / length_px : vec2(0.0);
                p.xy += side * UV2.y * 2.0 / VIEWPORT_SIZE * p.w;
                POSITION = p;
            }
        }
        void fragment() { ALBEDO = COLOR.rgb; ALPHA = 1.0; }
        """;
    private const string MarkerCode = """
        uniform vec4 tint : source_color = vec4(1.0);
        uniform float radius_pixels = 3.5;
        void vertex() {
            vec4 p = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0);
            p.xy += VERTEX.xy * radius_pixels * 2.0 / VIEWPORT_SIZE * p.w;
            POSITION = p;
        }
        void fragment() {
            float radius = length(UV * 2.0 - 1.0);
            if (radius > 1.0) discard;
            ALBEDO = tint.rgb;
            ALPHA = 1.0;
        }
        """;
    public static void Segment(ImmediateMesh mesh, Vector3 a, Vector3 b, Color color)
    {
        void Vertex(Vector3 point, Vector3 other, float side)
        {
            mesh.SurfaceSetColor(color); mesh.SurfaceSetUV(new(other.X, other.Y)); mesh.SurfaceSetUV2(new(other.Z, side)); mesh.SurfaceAddVertex(point);
        }
        Vertex(a, b, 1); Vertex(a, b, -1); Vertex(b, a, -1);
        Vertex(b, a, -1); Vertex(a, b, -1); Vertex(b, a, 1);
    }
}
