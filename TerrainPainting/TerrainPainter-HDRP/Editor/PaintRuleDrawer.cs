using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainPainting.EditorTools
{
    /// <summary>
    /// Draws a paint rule as one line when collapsed and the controls that matter when
    /// open, with the rest behind an Advanced disclosure.
    ///
    /// The default array drawer showed every field on every rule at once: twelve
    /// controls each, so four rules filled the Inspector with fifty of them and nothing
    /// indicated which three actually needed attention. Every field is still reachable -
    /// this only changes what is visible before you ask for it.
    /// </summary>
    [CustomPropertyDrawer(typeof(PaintRule))]
    public class PaintRuleDrawer : PropertyDrawer
    {
        const float Line = 18f;
        const float Gap = 2f;
        const float SwatchWidth = 34f;

        static GUIStyle _summaryStyle, _disabledNameStyle, _hintStyle;

        // EditorStyles is unavailable during static initialization, hence the lazy get.
        static GUIStyle SummaryStyle => _summaryStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.55f, 0.55f, 0.55f) }
        };

        static GUIStyle DisabledNameStyle => _disabledNameStyle ??= new GUIStyle(EditorStyles.label)
        {
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
        };

        static GUIStyle HintStyle => _hintStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            fontStyle = FontStyle.Italic,
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
        };

        /// <summary>
        /// Rows this rule draws, which is what its height is measured from.
        ///
        /// Counting here rather than in GetPropertyHeight keeps one number in one place:
        /// the height and the drawing have to agree exactly, and when they were counted
        /// separately a row added to the drawing was missed by the measurement, so rules
        /// drew over each other.
        /// </summary>
        static int RowCount(SerializedProperty property)
        {
            // Collapsed: the header line only.
            if (!property.isExpanded) return 1;

            // Header, terrain layer, base toggle, height, slope, softness, strength.
            int rows = 7;

            // The Advanced foldout is always drawn when the rule is open.
            rows += 1;

            if (AdvancedOpen(property))
            {
                // Shape, its caption, wetness, its caption, the noise toggle.
                rows += 5;

                // Patch size, amount, variation.
                if (property.FindPropertyRelative("useNoiseMask").boolValue) rows += 3;
            }

            return rows;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = RowCount(property) * (Line + Gap);

            // The gap below the header, and a little breathing room under the rule so
            // consecutive rules do not read as one block.
            if (property.isExpanded) height += Gap * 3f;

            return height;
        }

        public override void OnGUI(Rect rect, SerializedProperty property, GUIContent label)
        {
            var enabled = property.FindPropertyRelative("enabled");
            var name = property.FindPropertyRelative("name");
            var layer = property.FindPropertyRelative("layer");

            var row = new Rect(rect.x, rect.y, rect.width, Line);

            DrawHeader(row, property, enabled, name, layer);

            if (!property.isExpanded) return;

            // Everything below is meaningless for a rule that will not paint, but it
            // stays editable: disabling a rule to come back to it later is the point.
            using (new EditorGUI.DisabledScope(!enabled.boolValue))
            {
                row.y += Line + Gap + Gap;
                EditorGUI.indentLevel++;

                row.y = DrawEssentials(row, property, layer);
                row.y = DrawAdvanced(row, property);

                EditorGUI.indentLevel--;
            }

            // Drawing past the reserved rect is what makes rules overlap each other, and
            // it is invisible in code review: the two counts simply drift apart. Half a
            // row of slack absorbs rounding without hiding a real miscount.
            if (row.y > rect.yMax + Line * 0.5f)
            {
                Debug.LogWarning(
                    $"PaintRuleDrawer drew {row.y - rect.yMax:0}px past its reserved height. " +
                    "RowCount is out of step with the rows OnGUI draws.");
            }
        }

        /// <summary>
        /// The collapsed line: enable toggle, name, layer swatch, and a summary of what
        /// the rule claims, so a closed rule still says what it does.
        /// </summary>
        void DrawHeader(
            Rect row,
            SerializedProperty property,
            SerializedProperty enabled,
            SerializedProperty name,
            SerializedProperty layer)
        {
            var foldoutRect = new Rect(row.x, row.y, 14f, row.height);
            property.isExpanded = EditorGUI.Foldout(foldoutRect, property.isExpanded, GUIContent.none);

            var toggleRect = new Rect(row.x + 14f, row.y, 16f, row.height);
            enabled.boolValue = EditorGUI.Toggle(toggleRect, enabled.boolValue);

            float summaryWidth = property.isExpanded ? 0f : Mathf.Min(190f, row.width * 0.45f);
            float nameWidth = row.width - 30f - SwatchWidth - summaryWidth - 8f;

            var nameRect = new Rect(row.x + 32f, row.y, Mathf.Max(40f, nameWidth), row.height);

            // The name is the only identifier in a collapsed list, so it is editable
            // right here rather than only once the rule is open.
            string shown = string.IsNullOrEmpty(name.stringValue) ? "Layer" : name.stringValue;
            if (enabled.boolValue)
            {
                name.stringValue = EditorGUI.TextField(nameRect, shown, EditorStyles.label);
            }
            else
            {
                EditorGUI.LabelField(nameRect, shown + "  (off)", DisabledNameStyle);
            }

            var swatchRect = new Rect(nameRect.xMax + 4f, row.y + 1f, SwatchWidth, row.height - 2f);
            DrawLayerSwatch(swatchRect, layer);

            if (!property.isExpanded && summaryWidth > 0f)
            {
                var summaryRect = new Rect(swatchRect.xMax + 4f, row.y, summaryWidth, row.height);
                EditorGUI.LabelField(summaryRect, Summarise(property), SummaryStyle);
            }
        }

        /// <summary>
        /// Shows the layer's own diffuse texture. A terrain layer's name is often
        /// something like "TL_Rock_02", which says far less than the texture itself.
        /// </summary>
        static void DrawLayerSwatch(Rect rect, SerializedProperty layer)
        {
            var terrainLayer = layer.objectReferenceValue as TerrainLayer;

            if (terrainLayer != null && terrainLayer.diffuseTexture != null)
            {
                EditorGUI.DrawPreviewTexture(rect, terrainLayer.diffuseTexture);
                return;
            }

            // Missing layers are the most common setup mistake, and a rule with none
            // blocks painting entirely, so it is called out rather than left blank.
            EditorGUI.DrawRect(rect, new Color(0.22f, 0.18f, 0.18f));
            EditorGUI.LabelField(rect, terrainLayer == null ? " none" : " no tex", EditorStyles.miniLabel);
        }

        /// <summary>
        /// Height, slope, blend and strength: the four that get touched on almost every
        /// rule. Each band's toggle sits inline with its range rather than on its own
        /// line, so turning one off does not cost a row.
        /// </summary>
        float DrawEssentials(Rect row, SerializedProperty property, SerializedProperty layer)
        {
            EditorGUI.PropertyField(row, layer, new GUIContent("Terrain Layer"));
            row.y += Line + Gap;

            EditorGUI.PropertyField(
                row,
                property.FindPropertyRelative("isBaseLayer"),
                new GUIContent("Base Layer", "Fills anything the other rules do not claim."));
            row.y += Line + Gap;

            DrawBandRow(
                row,
                property.FindPropertyRelative("useHeight"),
                property.FindPropertyRelative("heightRange"),
                "Height", 0f, 1f, "{0:0.00}");
            row.y += Line + Gap;

            DrawBandRow(
                row,
                property.FindPropertyRelative("useSlope"),
                property.FindPropertyRelative("slopeRange"),
                "Slope", 0f, 90f, "{0:0}°");
            row.y += Line + Gap;

            EditorGUI.PropertyField(row, property.FindPropertyRelative("blend"), new GUIContent("Edge Softness"));
            row.y += Line + Gap;

            EditorGUI.PropertyField(row, property.FindPropertyRelative("strength"));
            row.y += Line + Gap;

            return row.y;
        }

        float DrawAdvanced(Rect row, SerializedProperty property)
        {
            string key = AdvancedKey(property);
            bool open = EditorPrefs.GetBool(key, false);

            var headerRect = new Rect(row.x, row.y, row.width, Line);
            bool nowOpen = EditorGUI.Foldout(headerRect, open, "Advanced", true);
            if (nowOpen != open) EditorPrefs.SetBool(key, nowOpen);

            row.y += Line + Gap;
            if (!nowOpen) return row.y;

            EditorGUI.indentLevel++;

            DrawBandRow(
                row,
                property.FindPropertyRelative("useCurvature"),
                property.FindPropertyRelative("curvatureRange"),
                "Shape", -1f, 1f, "{0:0.00}");
            row.y += Line + Gap;

            DrawCaption(row, "-1 valley · 0 flat · +1 ridge");
            row.y += Line + Gap;

            DrawBandRow(
                row,
                property.FindPropertyRelative("useFlow"),
                property.FindPropertyRelative("flowRange"),
                "Wetness", 0f, 1f, "{0:0.00}");
            row.y += Line + Gap;

            DrawCaption(row, "0 sheds water · 1 collects it");
            row.y += Line + Gap;

            var useNoise = property.FindPropertyRelative("useNoiseMask");
            EditorGUI.PropertyField(new Rect(row.x, row.y, row.width, Line), useNoise, new GUIContent("Break Up Edges"));
            row.y += Line + Gap;

            if (useNoise.boolValue)
            {
                EditorGUI.indentLevel++;

                EditorGUI.PropertyField(
                    new Rect(row.x, row.y, row.width, Line),
                    property.FindPropertyRelative("noiseSize"),
                    new GUIContent("Patch Size (m)"));
                row.y += Line + Gap;

                EditorGUI.PropertyField(
                    new Rect(row.x, row.y, row.width, Line),
                    property.FindPropertyRelative("noiseStrength"),
                    new GUIContent("Amount"));
                row.y += Line + Gap;

                EditorGUI.PropertyField(
                    new Rect(row.x, row.y, row.width, Line),
                    property.FindPropertyRelative("noiseSeed"),
                    new GUIContent("Variation"));
                row.y += Line + Gap;

                EditorGUI.indentLevel--;
            }

            EditorGUI.indentLevel--;
            return row.y;
        }

        /// <summary>
        /// A hint under a band, aligned with the slider rather than the window edge, so
        /// it reads as describing that control instead of floating beside it.
        /// </summary>
        static void DrawCaption(Rect row, string text)
        {
            float indent = EditorGUI.indentLevel * 15f;
            float labelColumn = Mathf.Max(52f, EditorGUIUtility.labelWidth - indent);

            var rect = new Rect(row.x + labelColumn + 20f, row.y, row.width - labelColumn - 20f, Line);
            EditorGUI.LabelField(rect, text, HintStyle);
        }

        /// <summary>
        /// A band on one line: a checkbox for whether it applies, then its range. The
        /// range is greyed rather than hidden when off, so the values you set are still
        /// there when you switch it back on.
        /// </summary>
        static void DrawBandRow(
            Rect row,
            SerializedProperty use,
            SerializedProperty range,
            string label,
            float min,
            float max,
            string format)
        {
            // GetControlRect already insets by the indent, while labelWidth is measured
            // from the unindented edge. Subtracting the indent keeps the label column
            // the same width as the fields above it instead of pushing it right.
            float indent = EditorGUI.indentLevel * 15f;
            float labelColumn = Mathf.Max(52f, EditorGUIUtility.labelWidth - indent);

            var labelRect = new Rect(row.x, row.y, labelColumn, Line);
            EditorGUI.LabelField(labelRect, label);

            var toggleRect = new Rect(labelRect.xMax, row.y, 16f, Line);
            use.boolValue = EditorGUI.Toggle(toggleRect, use.boolValue);

            using (new EditorGUI.DisabledScope(!use.boolValue))
            {
                var value = range.vector2Value;
                float lo = Mathf.Min(value.x, value.y);
                float hi = Mathf.Max(value.x, value.y);

                // The numbers get a column of their own. Drawn over the slider they sat
                // on top of the handles they describe, which is unreadable at any width
                // and hides the very handles being dragged.
                const float ValueColumn = 74f;

                float sliderX = toggleRect.xMax + 4f;
                float available = row.xMax - sliderX;
                float sliderWidth = Mathf.Max(40f, available - ValueColumn);

                var sliderRect = new Rect(sliderX, row.y, sliderWidth, Line);

                // MinMaxSlider rather than two fields: the pair is one band, and dragging
                // its ends is how a band actually gets tuned.
                EditorGUI.MinMaxSlider(sliderRect, ref lo, ref hi, min, max);

                // Only drawn where there is honestly room: squeezed into a few pixels the
                // text clips mid-number and reads as a different value.
                if (available - sliderWidth >= 40f)
                {
                    var valueRect = new Rect(sliderRect.xMax + 4f, row.y, row.xMax - sliderRect.xMax - 4f, Line);
                    string text = use.boolValue
                        ? string.Format(format, lo) + " – " + string.Format(format, hi)
                        : "any";

                    EditorGUI.LabelField(valueRect, text, SummaryStyle);
                }

                range.vector2Value = new Vector2(lo, hi);
            }
        }

        /// <summary>
        /// One-line description of what the rule claims, for the collapsed row. Only
        /// the bands that are on appear, since an off band constrains nothing.
        /// </summary>
        static string Summarise(SerializedProperty property)
        {
            var parts = new System.Collections.Generic.List<string>(4);

            if (property.FindPropertyRelative("useHeight").boolValue)
            {
                var r = property.FindPropertyRelative("heightRange").vector2Value;
                parts.Add($"{Mathf.Min(r.x, r.y) * 100f:0}–{Mathf.Max(r.x, r.y) * 100f:0}%");
            }

            if (property.FindPropertyRelative("useSlope").boolValue)
            {
                var r = property.FindPropertyRelative("slopeRange").vector2Value;
                parts.Add($"{Mathf.Min(r.x, r.y):0}–{Mathf.Max(r.x, r.y):0}°");
            }

            if (property.FindPropertyRelative("useCurvature").boolValue)
            {
                var r = property.FindPropertyRelative("curvatureRange").vector2Value;
                float mid = (r.x + r.y) * 0.5f;
                parts.Add(mid < -0.15f ? "valleys" : mid > 0.15f ? "ridges" : "flat");
            }

            if (property.FindPropertyRelative("useFlow").boolValue)
            {
                var r = property.FindPropertyRelative("flowRange").vector2Value;
                parts.Add(Mathf.Min(r.x, r.y) > 0.4f ? "wet" : "dry");
            }

            if (property.FindPropertyRelative("isBaseLayer").boolValue)
            {
                // A base layer covers the gaps whatever its bands say, so its summary
                // has to lead with that rather than listing bands that only add weight.
                return parts.Count == 0 ? "base · everywhere" : "base · " + string.Join(" · ", parts);
            }

            return parts.Count == 0 ? "everywhere" : string.Join(" · ", parts);
        }

        /// <summary>
        /// Advanced open state is remembered per rule position. Keying on the property
        /// path means reordering the array carries the state to whatever rule takes that
        /// slot, which is harmless: it is a disclosure, not a value.
        /// </summary>
        static string AdvancedKey(SerializedProperty property) =>
            "CustomTools.TerrainPainting.rule.advanced." + property.propertyPath;

        static bool AdvancedOpen(SerializedProperty property) =>
            EditorPrefs.GetBool(AdvancedKey(property), false);
    }
}
