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
        layout(push_constant) uniform Push { mat4 viewProj; vec4 lightDir; vec4 right; vec4 up; } pc;
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
        layout(push_constant) uniform Push { mat4 viewProj; vec4 lightDir; vec4 right; vec4 up; } pc;
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
        layout(push_constant) uniform Push { mat4 viewProj; vec4 lightDir; vec4 right; vec4 up; } pc;
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

    public static ReadOnlySpan<byte> SpriteVertex => """
        #version 450
        layout(push_constant) uniform Push { mat4 viewProj; vec4 lightDir; vec4 right; vec4 up; } pc;
        layout(location = 0) in vec3 center;
        layout(location = 1) in float radius;
        layout(location = 2) in vec4 color;
        layout(location = 0) out vec2 local;
        layout(location = 1) out vec4 vColor;
        const vec2 corners[6] = vec2[](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(-1, -1), vec2(1, 1), vec2(-1, 1));
        void main()
        {
            vec2 q = corners[gl_VertexIndex];
            local = q;
            vColor = color;
            vec3 w = center + (pc.right.xyz * q.x + pc.up.xyz * q.y) * radius;
            gl_Position = pc.viewProj * vec4(w, 1.0);
        }
        """u8;

    /// <summary>Un disque doux : cœur brillant, bord qui s'éteint ; ajouté à ce qui est déjà dessiné.</summary>
    public static ReadOnlySpan<byte> SpriteFragment => """
        #version 450
        layout(location = 0) in vec2 local;
        layout(location = 1) in vec4 vColor;
        layout(location = 0) out vec4 outColor;
        void main()
        {
            float d = dot(local, local);
            if (d > 1.0)
                discard;
            float a = exp(-d * 5.0) * (1.0 - d);
            outColor = vec4(vColor.rgb * vColor.a * a, 0.0);
        }
        """u8;

    /// <summary>Un triangle qui couvre l'écran ; uv (0, 0) en haut à gauche.</summary>
    public static ReadOnlySpan<byte> FullscreenVertex => """
        #version 450
        layout(location = 0) out vec2 uv;
        void main()
        {
            vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
            uv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """u8;

    const string BloomCommon = """
        #version 450
        layout(location = 0) in vec2 uv;
        layout(set = 0, binding = 0) uniform sampler2D src;
        layout(push_constant) uniform Post { vec4 p; } pc;   // x, y : texel de la source ; z : seuil ou intensité ; w : exposition
        layout(location = 0) out vec4 outColor;
        vec3 box4(vec2 at)
        {
            vec2 t = pc.p.xy;
            return 0.25 * (texture(src, at + vec2(-t.x, -t.y)).rgb + texture(src, at + vec2(t.x, -t.y)).rgb
                         + texture(src, at + vec2(-t.x, t.y)).rgb + texture(src, at + vec2(t.x, t.y)).rgb);
        }
        """;

    /// <summary>Première réduction, avec seuil doux : seul ce qui dépasse le seuil déborde.</summary>
    public static ReadOnlySpan<byte> BloomPrefilter => System.Text.Encoding.UTF8.GetBytes(BloomCommon + """
        void main()
        {
            vec3 c = min(box4(uv), vec3(64.0));
            float threshold = pc.p.z;
            float knee = threshold * 0.5 + 1e-4;
            float b = max(c.r, max(c.g, c.b));
            float soft = clamp(b - threshold + knee, 0.0, 2.0 * knee);
            soft = soft * soft / (4.0 * knee);
            float contribution = max(soft, b - threshold) / max(b, 1e-4);
            outColor = vec4(c * contribution, 1.0);
        }
        """);

    public static ReadOnlySpan<byte> BloomDown => System.Text.Encoding.UTF8.GetBytes(BloomCommon + """
        void main() { outColor = vec4(box4(uv), 1.0); }
        """);

    /// <summary>Agrandissement en tente (9 échantillons), ajouté au niveau plus grand.</summary>
    public static ReadOnlySpan<byte> BloomUp => System.Text.Encoding.UTF8.GetBytes(BloomCommon + """
        void main()
        {
            vec2 t = pc.p.xy;
            vec3 c = texture(src, uv).rgb * 4.0;
            c += (texture(src, uv + vec2(-t.x, 0)).rgb + texture(src, uv + vec2(t.x, 0)).rgb
                + texture(src, uv + vec2(0, -t.y)).rgb + texture(src, uv + vec2(0, t.y)).rgb) * 2.0;
            c += texture(src, uv + vec2(-t.x, -t.y)).rgb + texture(src, uv + vec2(t.x, -t.y)).rgb
               + texture(src, uv + vec2(-t.x, t.y)).rgb + texture(src, uv + vec2(t.x, t.y)).rgb;
            outColor = vec4(c / 16.0, 1.0);
        }
        """);

    /// <summary>
    /// Composition : scène + bloom, exposition, puis tonemapping qui ne touche pas aux couleurs sous 0,8 (interface,
    /// grille, coques éclairées) et comprime doucement au-delà, sans jamais saturer d'un coup.
    /// </summary>
    public static ReadOnlySpan<byte> Composite => """
        #version 450
        layout(location = 0) in vec2 uv;
        layout(set = 0, binding = 0) uniform sampler2D hdr;
        layout(set = 0, binding = 1) uniform sampler2D bloom;
        layout(push_constant) uniform Post { vec4 p; } pc;
        layout(location = 0) out vec4 outColor;
        vec3 tonemap(vec3 x)
        {
            const float k = 0.8;
            vec3 over = k + (1.0 - k) * (1.0 - exp(-(x - k) / (1.0 - k)));
            return mix(x, over, step(vec3(k), x));
        }
        void main()
        {
            vec3 c = texture(hdr, uv).rgb + texture(bloom, uv).rgb * pc.p.z;
            outColor = vec4(tonemap(max(c * pc.p.w, vec3(0.0))), 1.0);
        }
        """u8;
}
