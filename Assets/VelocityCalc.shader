Shader "Hidden/VelocityCalc"
{
    Properties
    {
        // 入力テクスチャ群
        _HeightMap ("HeightMap (R channel = height)", 2D) = "white" {}    // 油の厚みをRチャンネルに格納している想定
        _NormalMap ("NormalMap (RGB)", 2D) = "bump" {}                    // HeightMap から生成した法線マップ（0..1表現）
        _PanHeight ("PanHeight (mask/height)", 2D) = "white" {}           // フライパン内領域のマスクまたは縁の高さ（0で外側）
        
        // C# 側から渡すパラメータ
        // 注意: このシェーダでは _Gravity.x = panLocal.x, _Gravity.y = panLocal.z を期待する（C# 側の割当と厳密に一致させること）
        _Gravity ("Gravity (x = pan.x, y = pan.z)", Vector) = (0,0,0,0)
        
        // Texel 単位（1/width, 1/height） — 勾配の計算で使用
        _TexelSize ("TexelSize (1/width,1/height)", Vector) = (0.01,0.01,0,0)

        // チューニング用パラメータ
        _kSlope ("Slope Gain (勾配に対する感度)", Float) = 0.6
        _kGravity ("Gravity Gain (重力寄与の強さ)", Float) = 1.0
        _maxVel ("Max Velocity (速度上限)", Float) = 0.6
        _Damping ("Damping (数値減衰 0..1)", Float) = 0.95
    }

    SubShader
    {
        // フルスクリーン Blit 用。深度やカリングは不要なので無効化。
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            ZWrite Off
            Cull Off
            ZTest Always

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "UnityCG.cginc"

            // --- テクスチャ／パラメータ宣言 ---
            sampler2D _HeightMap;    // R = height
            sampler2D _NormalMap;    // RGB = normal (0..1)
            sampler2D _PanHeight;    // R = rim mask or rim height (0 = outside)

            // _Gravity は C# が (pan.x, pan.z) を x,y に入れて渡す想定
            float4 _Gravity;
            float4 _TexelSize;  // x = 1/width, y = 1/height

            float _kSlope;
            float _kGravity;
            float _maxVel;
            float _Damping;

            // --- 入力頂点構造体 / 出力構造体 ---
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 pos : SV_POSITION;
            };

            // 頂点シェーダ — フルスクリーン四角形をそのまま透過
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex); // 標準的なクリップ変換
                o.uv = v.uv;                             // UV をフラグメントに転送
                return o;
            }

            // --------------------
            // ヘルパー関数：Heightの安全サンプル
            // - Height が極端な値を持つと勾配や sqrt(h) で数値破綻が起きることがあるため saturate で 0..1 に収める
            // --------------------
            inline float SampleHeight(float2 uv)
            {
                float h = tex2D(_HeightMap, uv).r;
                return saturate(h); // 0..1 にクランプ（安全策）
            }

            // --------------------
            // ヘルパー関数：中央差分による勾配計算（texture-space）
            // - 戻り値は (dh/du, dh/dv)（UV 空間当たりの高さ差）
            // - _TexelSize によって適切なスケール（1/width,1/height）を渡す必要あり
            // - ex, ey はゼロ割避けのため小さな下限を設ける
            // --------------------
            inline float2 SampleHeightGradient(float2 uv)
            {
                // --- 基本の中央差分（高さから求める勾配） ---
                float2 ts = _TexelSize.xy;
                // テクセルサイズが 0 にならないようにフォールバック
                float ex = max(ts.x, 1e-6);
                float ey = max(ts.y, 1e-6);

                // 中央差分で周囲4点をサンプリング
                float hL = tex2D(_HeightMap, uv - float2(ex, 0)).r;
                float hR = tex2D(_HeightMap, uv + float2(ex, 0)).r;
                float hD = tex2D(_HeightMap, uv - float2(0, ey)).r;
                float hU = tex2D(_HeightMap, uv + float2(0, ey)).r;

                // 中央差分の公式
                float hx = (hR - hL) / (2.0 * ex);
                float hy = (hU - hD) / (2.0 * ey);
                float2 heightGrad = float2(hx, hy);
                return heightGrad;
                
                /*
                // --- フライパン高さ（PanHeight）からの中央差分 ---
                // _PanHeight は R チャンネルに「水平に置いたときのその地点の高さ」を格納している想定
                // 未設定でも tex2D は (1,1,1,1) を返すので結果は 0 になりやすいが、
                // 確実に動作させるため直接参照して差分をとる。
                float pL = tex2D(_PanHeight, uv - float2(ex, 0)).r;
                float pR = tex2D(_PanHeight, uv + float2(ex, 0)).r;
                float pD = tex2D(_PanHeight, uv - float2(0, ey)).r;
                float pU = tex2D(_PanHeight, uv + float2(0, ey)).r;
                float hx_pan = (pR - pL) / (2.0 * ex);
                float hy_pan = (pU - pD) / (2.0 * ey);
                float2 panGrad = float2(hx_pan, hy_pan);

                // --- 必要なら PanHeight のスケールを掛けたい場合（コメント解除して使用） ---
                // e.g. if PanHeight uses different units, declare _PanHeightScale in Properties and uncomment:
                // panGrad *= _PanHeightScale;

                // --- 合成：表面の総勾配 = 油の勾配 + フライパンの勾配 ---
                // （加算で表面高さの勾配を表す。重みをつけたい場合は panGrad を weight で乗じる等）
                float2 combinedGrad = heightGrad + panGrad;

                // --- 返り値 ---
                return combinedGrad;
                */
            }

            // --------------------
            // フラグメントシェーダ（ここが本体）
            // - 各ピクセル（UV）での速度ベクトル (vx, vy) を計算して RG に格納する
            // - 流速は「勾配項（低い方へ流す）」と「表面に沿った重力寄与」の和で見積もる
            // --------------------
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // --------------------
                // rim（縁）判定
                // - RimHeight テクスチャの R チャンネルが 0 以下なら「パン外」とみなして速度 0 を返す
                // - 運用上、デバッグ時は rim をすべて 1 にしてマスクしないと良い（内部確認用）
                // --------------------
                float rimH = tex2D(_PanHeight, uv).r;
                if (rimH <= 0.0) {
                    // 外側は速度無し（黒）
                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                // --------------------
                // 高さ h をサンプルして rim の高さで clamp
                // - h が非常に小さいと sqrt(h) により重力寄与がほぼ消える点に注意（必要なら最低 h_min を導入）
                // --------------------
                float h = SampleHeight(uv);
                h = min(h, rimH);

                // --------------------
                // 法線読み取り・正規化
                // - NormalMap は通常 0..1 に格納されるので *2-1 して -1..1 に戻す
                // - 必ず normalize してから使う（正規化忘れは投影で誤差を生む）
                // - 小さな数値での不安定を避けるため tiny を足しておく（normalize の安定化）
                // --------------------
                float4 n4 = tex2D(_NormalMap, uv);
                // n4.xyz in 0..1 space -> convert to -1..1
                float3 N = normalize(n4.xyz * 2.0 - 1.0 + 1e-6);

                // --------------------
                // 高さの勾配（uv空間）を取得
                // - grad は (dh/du, dh/dv) で、負号をつければ「低い方へ流れる」方向になる
                // --------------------
                float2 grad = SampleHeightGradient(uv);

                // --------------------
                // 重力ベクトルをテクスチャ座標系で直接扱う（2D計算）
                // - C# 側: _Gravity.x = パンX軸成分, _Gravity.y = パンZ軸成分
                // - テクスチャ座標系: U方向=X軸, V方向=Z軸として直接マッピング  
                // - 3D変換を避けて座標系の混乱を防ぐ
                // --------------------
                float2 g_2d = float2(_Gravity.x, _Gravity.y);
                
                // --------------------
                // 法線から表面の傾きを2Dで計算
                // - 法線 N.xy が表面の傾き方向を示している
                // - N.z（垂直成分）で正規化して接線面での重力成分を求める
                // --------------------
                float2 surface_tilt = N.xy / max(N.z, 0.001); // ゼロ除算防止
                float2 g_tangent = g_2d - surface_tilt * dot(g_2d, surface_tilt) / max(dot(surface_tilt, surface_tilt), 0.001);

                // --------------------
                // 速度の見積もり
                // - 勾配項: -_kSlope * grad    -> 低い方向へ移動させる
                // - 重力項: _kGravity * g_tangent * sqrt(h)
                //   sqrt(h) を掛けることで薄い液体では重力寄与が弱く、厚みが増すと流れやすくする
                //   （物理厳密性は妥協した経験則的なモデル）
                // - 数値安定化: sqrt の引数には小さな eps を入れる
                // --------------------
                float2 v = -_kSlope * grad + _kGravity * g_tangent * sqrt(max(h, 1e-6));

                // --------------------
                // ダンピングとクランプ（過大な値や高周波を抑える）
                // - v *= _Damping: 各フレームで少し減衰させることで振動を抑制
                // - 上限 _maxVel により極端な値を切る（安全策）
                // --------------------
                v *= _Damping;
                float len = length(v);
                if (len > _maxVel && len > 1e-9)
                    v = v * (_maxVel / len);

                // --------------------
                // 出力：R= vx, G= vy, B unused, A=1
                // - C# 側はこの RT を ReadPixels/GetPixels 等で読み、粒子の移動に使用する
                // --------------------
                return float4(v.x, v.y, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
