using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainGeneration.EditorTools
{
    /// <summary>
    /// Foldout section drawing shared by the settings Inspector. Sections remember
    /// whether they were open, and a collapsed one still shows its state in a summary
    /// on the right, so closing a section never hides what it is set to.
    /// </summary>
    public static class InspectorSections
    {
        // Built once on demand rather than per call: Foldout runs for every section on
        // every repaint, and orbiting the 3D preview repaints continuously.
        // EditorStyles is unavailable during static initialization, hence the lazy get.
        static GUIStyle _headerStyle, _summaryStyle, _hintStyle;

        static GUIStyle HeaderStyle => _headerStyle ??= new GUIStyle(EditorStyles.foldoutHeader)
        {
            fontStyle = FontStyle.Bold
        };

        static GUIStyle SummaryStyle => _summaryStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight,
            normal = { textColor = new Color(0.55f, 0.55f, 0.55f) }
        };

        static GUIStyle HintStyle => _hintStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            fontStyle = FontStyle.Italic,
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f) }
        };

        // Reused for width measurement: CalcSize needs a GUIContent, and allocating one
        // per section per repaint is exactly the churn the preview work removed.
        static readonly GUIContent MeasureContent = new GUIContent();

        static GUIContent TempContent(string text)
        {
            MeasureContent.text = text;
            return MeasureContent;
        }

        /// <summary>
        /// Trims a full-width control rect down to the value column, where a caption or
        /// a drawing should line up with the fields above it. EditorGUIUtility.labelWidth
        /// is measured from the unindented left edge, so using it raw inside a nested
        /// scope leaves the content misaligned and, at a narrow Inspector, clipped.
        /// </summary>
        public static Rect ValueColumn(Rect rect)
        {
            // GetControlRect already returns a rect inset by the current indent, while
            // labelWidth is measured from the unindented edge. So inside an indent the
            // label occupies labelWidth minus that inset, and adding labelWidth whole
            // would push the content one indent too far right.
            float indent = EditorGUI.indentLevel * 15f;
            float labelColumn = Mathf.Max(20f, EditorGUIUtility.labelWidth - indent);

            rect.xMin += labelColumn + 2f;

            // Keep a usable width at narrow Inspector sizes rather than inverting.
            if (rect.width < 40f) rect.xMin = Mathf.Max(rect.x, rect.xMax - 40f);

            return rect;
        }

        // EditorPrefs reads hit the registry, and Foldout runs for every section on
        // every repaint. The open state is cached in memory and written through only
        // when it actually changes.
        static readonly System.Collections.Generic.Dictionary<string, bool> OpenState =
            new System.Collections.Generic.Dictionary<string, bool>();

        static bool GetState(string prefKey, bool defaultValue)
        {
            if (OpenState.TryGetValue(prefKey, out bool cached)) return cached;

            bool stored = EditorPrefs.GetBool(prefKey, defaultValue);
            OpenState[prefKey] = stored;
            return stored;
        }

        static void SetState(string prefKey, bool value)
        {
            OpenState[prefKey] = value;
            EditorPrefs.SetBool(prefKey, value);
        }

        /// <summary>
        /// Draws a section header. Returns true when the body should be drawn.
        /// </summary>
        /// <param name="key">Stable key for remembering the open state.</param>
        /// <param name="label">Section title.</param>
        /// <param name="summary">
        /// Short state description shown on the right, e.g. "on · 50k". Keep it to a few
        /// characters: it is there to be glanced at, not read.
        /// </param>
        /// <param name="defaultOpen">Open state the first time this section is seen.</param>
        public static bool Foldout(string key, string label, string summary = null, bool defaultOpen = false)
        {
            string prefKey = $"CustomTools.TerrainGeneration.section.v2.{key}";
            bool open = GetState(prefKey, defaultOpen);

            var rect = GUILayoutUtility.GetRect(1f, EditorGUIUtility.singleLineHeight + 6f);

            bool newOpen = EditorGUI.BeginFoldoutHeaderGroup(rect, open, label, HeaderStyle);
            EditorGUI.EndFoldoutHeaderGroup();

            // Drawn after the header so it sits on top of the header's background,
            // and only when there is room left beside the title — at a narrow
            // Inspector width the summary is dropped rather than printed over the
            // section name.
            if (!string.IsNullOrEmpty(summary))
            {
                float titleWidth = HeaderStyle.CalcSize(TempContent(label)).x + 24f;
                float available = rect.width - titleWidth - 10f;

                if (available > 40f)
                {
                    float summaryWidth = Mathf.Min(
                        available, SummaryStyle.CalcSize(TempContent(summary)).x + 6f);

                    var summaryRect = rect;
                    summaryRect.xMin = rect.xMax - summaryWidth - 6f;
                    summaryRect.xMax = rect.xMax - 6f;

                    GUI.Label(summaryRect, summary, SummaryStyle);
                }
            }

            if (newOpen != open) SetState(prefKey, newOpen);

            return newOpen;
        }

        /// <summary>
        /// Draws a nested "Advanced" disclosure for the dials that only matter during a
        /// tuning session. Returns true when its body should be drawn.
        /// </summary>
        public static bool Advanced(string key, string hint = null)
        {
            string prefKey = $"CustomTools.TerrainGeneration.advanced.v2.{key}";
            bool open = GetState(prefKey, false);

            string label = string.IsNullOrEmpty(hint) || open ? "Advanced" : $"Advanced  ({hint})";

            // EditorStyles.foldout, not a label style: a label style draws no disclosure
            // triangle, which left these reading as inert text rather than something to
            // click. The indent comes from indentLevel so it lines up with the fields
            // around it instead of a hand-rolled Space.
            using (new EditorGUI.IndentLevelScope())
            {
                bool newOpen = EditorGUILayout.Foldout(open, label, true, EditorStyles.foldout);
                if (newOpen != open) SetState(prefKey, newOpen);
                open = newOpen;
            }

            return open;
        }

        /// <summary>
        /// A slider with a plainer label than the field name, plus a caption under it
        /// saying what the two ends mean. Abstract numbers are the hardest part of this
        /// Inspector to read, and a direction hint costs one line.
        /// </summary>
        public static void SliderWithHint(
            SerializedProperty property, string label, string lowHint, string highHint)
        {
            EditorGUILayout.PropertyField(property, new GUIContent(label));

            var rect = ValueColumn(EditorGUILayout.GetControlRect(false, 13f));
            GUI.Label(rect, $"{lowHint}  ←→  {highHint}", HintStyle);
        }
    }
}
