namespace Poiesis.Render.Vulkan;

/// <summary>
/// Shaders GLSL, compilés en SPIR-V au démarrage par ShaderC.
/// Les matrices de Brutal.Numerics sont en lignes (convention System.Numerics) : lues en colonnes par GLSL,
/// elles arrivent transposées, d'où l'ordre <c>M * v</c> qui équivaut à <c>v * M</c> côté C#.
/// </summary>
static class Shaders
{
    public static ReadOnlySpan<byte> MeshVertex => """
        #version 450
        layout(push_constant) uniform Push { mat4 viewProj; vec4 lightDir; } pc;
        layout(location = 0) in vec3 position;
        layout(location = 1) in vec3 normal;
        layout(location = 2) in vec2 texCoord;
        layout(location = 3) in mat4 world;     // emplacements 3 à 6
        layout(location = 7) in vec4 color;
        layout(location = 0) out vec3 vNormal;
        layout(location = 1) out vec4 vColor;
        layout(location = 2) out vec2 vTexCoord;
        void main()
        {
            vec4 wp = world * vec4(position, 1.0);
            gl_Position = pc.viewProj * wp;
            // Échelle non uniforme (taille par axe) : les normales suivent l'inverse transposée.
            vNormal = normalize(transpose(inverse(mat3(world))) * normal);
            vColor = color;
            vTexCoord = texCoord;
        }
        """u8;

    public static ReadOnlySpan<byte> MeshFragment => """
        #version 450
        layout(push_constant) uniform Push { mat4 viewProj; vec4 lightDir; } pc;
        layout(location = 0) in vec3 vNormal;
        layout(location = 1) in vec4 vColor;
        layout(location = 2) in vec2 vTexCoord;
        layout(set = 0, binding = 0) uniform sampler2D baseColor;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            vec4 albedo = vColor * texture(baseColor, vTexCoord);
            vec3 n = normalize(vNormal);
            float diffuse = max(dot(n, pc.lightDir.xyz), 0.0);
            float fill = max(dot(n, -pc.lightDir.xyz), 0.0) * 0.25;   // contre-jour discret
            vec3 lit = albedo.rgb * (0.18 + 0.82 * diffuse + fill);
            outColor = vec4(lit, albedo.a);
        }
        """u8;

    public static ReadOnlySpan<byte> LineVertex => """
        #version 450
        layout(push_constant) uniform Push { mat4 viewProj; vec4 lightDir; } pc;
        layout(location = 0) in vec3 position;
        layout(location = 1) in vec4 color;
        layout(location = 0) out vec4 vColor;
        void main()
        {
            gl_Position = pc.viewProj * vec4(position, 1.0);
            vColor = color;
        }
        """u8;

    public static ReadOnlySpan<byte> LineFragment => """
        #version 450
        layout(location = 0) in vec4 vColor;
        layout(location = 0) out vec4 outColor;
        void main() { outColor = vColor; }
        """u8;
}
