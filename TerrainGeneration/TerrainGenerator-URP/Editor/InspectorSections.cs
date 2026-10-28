using UnityEditor;
using UnityEngine;

namespace CustomTools.TerrainGenerationURP.EditorTools
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
            string prefKey = $"CustomTools.TerrainGeneration.section.{key}";
            bool open = GetState(prefKey, defaultOpen);

            var rect = GUILayoutUtility.GetRect(1f, EditorGUIUtility.singleLineHeight + 6f);

            // Draw the summary first so the foldout's own hit area stays on top;
            // otherwise clicking near the summary misses the toggle.
            if (!string.IsNullOrEmpty(summary))
            {
                var summaryRect = rect;
                summaryRect.xMin = rect.xMax - 160f;
                summaryRect.xMax = rect.xMax - 6f;

                GUI.Label(summaryRect, summary, SummaryStyle);
            }

            bool newOpen = EditorGUI.BeginFoldoutHeaderGroup(rect, open, label, HeaderStyle);
            EditorGUI.EndFoldoutHeaderGroup();

            if (newOpen != open) SetState(prefKey, newOpen);

            return newOpen;
        }

        /// <summary>
        /// Draws a nested "Advanced" disclosure for the dials that only matter during a
        /// tuning session. Returns true when its body should be drawn.
        /// </summary>
        public static bool Advanced(string key, string hint = null)
        {
            string prefKey = $"CustomTools.TerrainGeneration.advanced.{key}";
            bool open = GetState(prefKey, false);

            string label = string.IsNullOrEmpty(hint) || open ? "Advanced" : $"Advanced  ({hint})";

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12f);
                bool newOpen = EditorGUILayout.Foldout(open, label, true, EditorStyles.miniBoldLabel);
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

            var rect = EditorGUILayout.GetControlRect(false, 13f);
            rect.xMin += EditorGUIUtility.labelWidth + 2f;
            GUI.Label(rect, $"{lowHint}  ←→  {highHint}", HintStyle);
        }
    }
}
