Shader "Unlit/HeightToNormal"
{
    Properties
    {
        _MainTex ("Height Map", 2D) = "white" {}
        _Strength ("Normal Strength", Float) = 1.0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Strength;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            
            fixed4 frag (v2f i) : SV_Target
            {
                // 隣接ピクセルのUV座標を計算
                float2 texel = _MainTex_TexelSize.xy;
                float h_center = tex2D(_MainTex, i.uv).r;
                float h_l = tex2D(_MainTex, i.uv - float2(texel.x, 0)).r;
                float h_r = tex2D(_MainTex, i.uv + float2(texel.x, 0)).r;
                float h_d = tex2D(_MainTex, i.uv - float2(0, texel.y)).r;
                float h_u = tex2D(_MainTex, i.uv + float2(0, texel.y)).r;

                // 高さの差分から法線のx, y成分を計算
                float3 normal;
                normal.x = (h_l - h_r) * _Strength;
                normal.y = (h_d - h_u) * _Strength;
                normal.z = 1.0;

                // 正規化し、[0, 1]の範囲にマッピングして出力
                return fixed4(normalize(normal) * 0.5 + 0.5, 1.0);
            }
            ENDCG
        }
    }
}