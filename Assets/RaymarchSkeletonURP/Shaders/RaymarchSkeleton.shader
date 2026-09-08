Shader "Hidden/PREMIERE/RaymarchSkeleton"
{
    // Fullscreen raymarch pass, drawn by RaymarchSkeletonRendererFeature.
    // Ray origin/direction are reconstructed from the *actual* URP camera
    // (position + inverse view-projection), so the raymarched skeletons
    // sit correctly in the scene and respond normally to camera movement,
    // instead of the original's separate camPosition/camAngle uniforms.
    //
    // Uses Unity's built-in UNITY_MATRIX_I_VP / _WorldSpaceCameraPos rather
    // than custom camera uniforms specifically so this works unmodified
    // under single-pass instanced stereo (VR): those built-ins are already
    // per-eye arrays behind the stereo instancing macros below, so each eye
    // reconstructs its own ray with no extra C#-side plumbing.

    SubShader
    {
        // PreviewType=Plane keeps the Editor's automatic material preview
        // (Inspector + Project thumbnail, which renders the instant this
        // material exists, before RaymarchSkeletonRendererFeature ever runs)
        // on the cheapest possible mesh, since this shader ignores mesh
        // geometry entirely and draws a fullscreen triangle regardless.
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "PreviewType" = "Plane" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "RaymarchSkeleton"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_instancing // required for single-pass instanced stereo (VR)

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "RaymarchSkeletonCore.hlsl"

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID // carries the eye index in single-pass instanced XR
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                // Fullscreen triangle (no mesh needed)
                OUT.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                OUT.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return OUT;
            }

            float3 ComputeCameraRayDir(float2 uv)
            {
                // uv in [0,1] -> NDC [-1,1], far plane point, unproject.
                // UNITY_MATRIX_I_VP resolves to the current eye's inverse
                // view-projection under stereo instancing.
                float4 clipPos = float4(uv * 2.0 - 1.0, 1.0, 1.0);
            #if UNITY_UV_STARTS_AT_TOP
                clipPos.y = -clipPos.y;
            #endif
                float4 worldPos = mul(UNITY_MATRIX_I_VP, clipPos);
                worldPos /= worldPos.w;
                return normalize(worldPos.xyz - _WorldSpaceCameraPos.xyz);
            }

            float4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                float3 rayDir = ComputeCameraRayDir(IN.uv);
                float3 color = ShadeRay(_WorldSpaceCameraPos.xyz, rayDir);
                return float4(color, 1.0);
            }
            ENDHLSL
        }

        // Mono-in-VR duplicate pass: RaymarchSkeletonRendererFeature renders
        // the expensive pass above ONCE (not once per eye) into a plain
        // (non-array) intermediate texture when running single-pass
        // instanced XR with mono enabled, then draws THIS pass with
        // instanceCount=2 to cheaply copy that single image into both eyes'
        // slices of the real stereo target - a plain texture sample per
        // pixel, instead of re-running the full raymarch for the second eye.
        Pass
        {
            Name "RaymarchSkeletonMonoBlit"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                OUT.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return OUT;
            }

            TEXTURE2D(_RM_MonoSource);
            SAMPLER(sampler_RM_MonoSource);

            float4 Frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);
                // Same plain source for both eye instances - that's the
                // whole point, this is the "duplicate, don't recompute" step.
                return SAMPLE_TEXTURE2D_LOD(_RM_MonoSource, sampler_RM_MonoSource, IN.uv, 0);
            }
            ENDHLSL
        }
    }
}
