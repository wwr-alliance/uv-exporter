#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WWR
{
    public class UVExporter : EditorWindow
    {
        private static class Constants
        {
            public const int WINDOW_WIDTH = 860;
            public const int WINDOW_HEIGHT = 560;
            public const int PREVIEW_PANEL_WIDTH = 530;
            public const int PREVIEW_SIZE = 512;
            public const int TILE_RANGE_MIN = -5;
            public const int TILE_RANGE_MAX = 5;
            public const float DEGENERATE_TRIANGLE_AREA_THRESHOLD = 0.00001f;
            public const float TILE_BOUNDARY_EPSILON = 0.001f;
            public const float LINE_THICKNESS = 1.0f;
            public const int DEFAULT_GRID_SIZE = 32;
            public const int MIN_GRID_SIZE = 8;
            public const int MAX_PREVIEW_TILES = 16;
            public const int MAX_EXPORT_PIXELS = 8192;
        }
        private Mesh _targetMesh;
        private int _resolutionIndex = 1;
        private readonly int[] _resolutionOptions = { 512, 1024, 2048, 4096, 8192 };
        private bool _drawBackground = false;
        private Color _backgroundColor = Color.white;
        private bool _drawLines = true;
        private Color _lineColor = Color.black;
        private bool _fillTriangles = false;
        private Color _triangleColor = Color.gray;
        private int _uvChannel = 0;
        private int _submeshIndex = -1;
        private bool _autoDetectTiles = true;
        private Vector2Int _manualMinTile = Vector2Int.zero;
        private Vector2Int _manualMaxTile = Vector2Int.zero;
        private Texture2D _previewTexture;
        private Vector2Int _detectedMinTile;
        private Vector2Int _detectedMaxTile;
        private string _lastSavedPath = "Assets";
        private int _lastSettingsHash;
        private int _gameObjectPickerControlID;
        private Material _backgroundMaterial;
        private bool _previewMaterial = true;
        private Material[] _sourceMaterials;
        private int _materialIndex = 0;
        private GameObject _sourceGameObject;
        private Vector2 _scrollPosition = Vector2.zero;

        // Cache for performance optimization
        private Vector2[][] _cachedUVs = new Vector2[8][];
        private int[] _cachedTriangles;
        private bool[] _hasUVChannels = new bool[8];
        private string[] _uvChannelOptions;
        private int[] _uvChannelIndices;
        private string[] _submeshOptions;
        private string[] _materialOptions;

        [MenuItem("Tools/WWR/UV Exporter")]
        public static void ShowWindow()
        {
            var window = GetWindow<UVExporter>("WWR UV Exporter");
            window.minSize = new Vector2(Constants.WINDOW_WIDTH, Constants.WINDOW_HEIGHT);
            //window.maxSize = window.minSize;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.Space(10);
                DrawPreviewPanel();
                EditorGUILayout.Space(10);
                DrawRightPanel();
                EditorGUILayout.Space(10);
            }

            // Update preview after all UI has been drawn
            if (ShouldUpdatePreview())
            {
                UpdatePreview();
            }
        }

        private void DrawPreviewPanel()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(Constants.PREVIEW_SIZE)))
            {
                // UV Preview box
                if (_previewTexture != null)
                {
                    Rect previewRect = GUILayoutUtility.GetRect(Constants.PREVIEW_SIZE, Constants.PREVIEW_SIZE);
                    EditorGUI.DrawPreviewTexture(previewRect, _previewTexture, null, ScaleMode.ScaleToFit);
                }
                else
                {
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(Constants.PREVIEW_SIZE), GUILayout.Height(Constants.PREVIEW_SIZE)))
                    {
                        GUILayout.FlexibleSpace();
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.FlexibleSpace();

                            if (_targetMesh == null)
                            {
                                EditorGUILayout.HelpBox("Select a GameObject or Mesh to preview UV layout", MessageType.Info);
                            }
                            else if (!HasUVChannel(_targetMesh, _uvChannel))
                            {
                                EditorGUILayout.HelpBox("No UVs found in selected channel", MessageType.Warning);
                            }
                            else if (IsTileCountExceeded())
                            {
                                Vector2Int minTile = _autoDetectTiles ? _detectedMinTile : _manualMinTile;
                                Vector2Int maxTile = _autoDetectTiles ? _detectedMaxTile : _manualMaxTile;
                                int tilesX = maxTile.x - minTile.x + 1;
                                int tilesY = maxTile.y - minTile.y + 1;
                                int totalTiles = tilesX * tilesY;
                                EditorGUILayout.HelpBox($"Preview disabled: Too many tiles ({totalTiles} > {Constants.MAX_PREVIEW_TILES})\nExport is still available", MessageType.Warning);
                            }
                            else
                            {
                                EditorGUILayout.HelpBox("Preview unavailable\nTry closing and reopening this window", MessageType.Warning);
                            }

                            GUILayout.FlexibleSpace();
                        }
                        GUILayout.FlexibleSpace();
                    }
                }

                EditorGUILayout.Space(4);

                // Material preview toggle and dropdown
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    _previewMaterial = EditorGUILayout.Toggle(_previewMaterial, GUILayout.Width(14));
                    EditorGUILayout.LabelField("Preview Material", GUILayout.Width(100));

                    if (EditorGUI.EndChangeCheck())
                    {
                        _lastSettingsHash = 0;
                    }

                    bool hasOptions = _materialOptions != null && _materialOptions.Length > 0;
                    EditorGUI.BeginDisabledGroup(!hasOptions || _materialOptions.Length == 1);
                    EditorGUI.BeginChangeCheck();
                    int currentIndex = hasOptions ? _materialIndex : 0;
                    string[] options = hasOptions ? _materialOptions : new[] { "(No Materials)" };
                    int newIndex = EditorGUILayout.Popup(currentIndex, options);
                    if (EditorGUI.EndChangeCheck() && hasOptions)
                    {
                        _materialIndex = newIndex;
                        if (_materialIndex >= 0 && _materialIndex < _sourceMaterials.Length)
                        {
                            _backgroundMaterial = _sourceMaterials[_materialIndex];
                            _lastSettingsHash = 0;
                        }
                    }
                    EditorGUI.EndDisabledGroup();
                }
            }
        }

        private void DrawRightPanel()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Height(Constants.WINDOW_HEIGHT - 26)))
            {
                HandleGameObjectPicker();
                DrawMeshSelection();
                GUILayout.Space(10);
                DrawChannelSubmeshSettings();
                GUILayout.Space(10);
                DrawScrollableSettings();
                DrawExportSection();
            }
        }

        private void HandleGameObjectPicker()
        {
            if ((Event.current.commandName == "ObjectSelectorUpdated" || Event.current.commandName == "ObjectSelectorClosed")
                && EditorGUIUtility.GetObjectPickerControlID() == _gameObjectPickerControlID)
            {
                GameObject selected = EditorGUIUtility.GetObjectPickerObject() as GameObject;
                if (selected != null)
                {
                    LoadGameObjectComponents(selected);
                }
                Repaint();
            }
        }

        private void LoadGameObjectComponents(GameObject go)
        {
            SkinnedMeshRenderer smr = go.GetComponent<SkinnedMeshRenderer>();
            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (smr != null || mf != null)
            {
                _targetMesh = smr != null ? smr.sharedMesh : mf.sharedMesh;
                _sourceGameObject = go;
                UpdateMeshCache();

                // Store all materials from the renderer
                if (smr != null)
                {
                    _sourceMaterials = smr.sharedMaterials;
                }
                else if (mf != null)
                {
                    MeshRenderer mr = go.GetComponent<MeshRenderer>();
                    if (mr != null)
                    {
                        _sourceMaterials = mr.sharedMaterials;
                    }
                }

                // Update submesh and material options after materials are loaded
                UpdateSubmeshOptions();
                _lastSettingsHash = 0;
            }
        }

        private void DrawMeshSelection()
        {
            // GameObject picker (no label, full width)
            EditorGUI.BeginChangeCheck();
            GameObject newGameObject = EditorGUILayout.ObjectField(_sourceGameObject, typeof(GameObject), true) as GameObject;
            if (EditorGUI.EndChangeCheck())
            {
                if (newGameObject != null)
                {
                    LoadGameObjectComponents(newGameObject);
                }
                else
                {
                    // GameObject cleared - clear mesh if it was set from GameObject
                    _sourceGameObject = null;
                    _targetMesh = null;
                    ClearMeshCache();
                    _sourceMaterials = null;
                    _backgroundMaterial = null;
                    _lastSettingsHash = 0;

                    if (_previewTexture != null)
                    {
                        DestroyImmediate(_previewTexture);
                        _previewTexture = null;
                    }
                }
            }

            // Mesh picker (no label, full width, disabled if set from GameObject)
            EditorGUI.BeginDisabledGroup(_sourceGameObject != null);
            EditorGUI.BeginChangeCheck();
            Mesh newMesh = EditorGUILayout.ObjectField(_targetMesh, typeof(Mesh), false) as Mesh;
            if (EditorGUI.EndChangeCheck())
            {
                _targetMesh = newMesh;
                UpdateMeshCache();
                _lastSettingsHash = 0;

                if (_targetMesh == null)
                {
                    if (_previewTexture != null)
                    {
                        DestroyImmediate(_previewTexture);
                        _previewTexture = null;
                    }
                }
            }
            EditorGUI.EndDisabledGroup();

            // Selection buttons
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select from Scene", GUILayout.Height(30)))
                {
                    _gameObjectPickerControlID = GUIUtility.GetControlID(FocusType.Passive);
                    EditorGUIUtility.ShowObjectPicker<GameObject>(null, true, "t:SkinnedMeshRenderer t:MeshFilter", _gameObjectPickerControlID);
                }

                EditorGUI.BeginDisabledGroup(_sourceGameObject == null && _targetMesh == null);
                if (GUILayout.Button("Reset", GUILayout.Height(30)))
                {
                    _sourceGameObject = null;
                    _targetMesh = null;
                    ClearMeshCache();
                    _sourceMaterials = null;
                    _backgroundMaterial = null;
                    _lastSettingsHash = 0;

                    if (_previewTexture != null)
                    {
                        DestroyImmediate(_previewTexture);
                        _previewTexture = null;
                    }
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawChannelSubmeshSettings()
        {
            // UV Channel dropdown
            if (_targetMesh != null && _uvChannelOptions != null && _uvChannelOptions.Length > 0)
            {
                int currentIndex = System.Array.IndexOf(_uvChannelIndices, _uvChannel);
                if (currentIndex == -1)
                {
                    _uvChannel = _uvChannelIndices[0];
                    currentIndex = 0;
                }

                EditorGUI.BeginDisabledGroup(_uvChannelOptions.Length == 1);
                int newIndex = EditorGUILayout.Popup("UV Channel", currentIndex, _uvChannelOptions);
                _uvChannel = _uvChannelIndices[newIndex];
                EditorGUI.EndDisabledGroup();
            }
            else if (_targetMesh != null)
            {
                EditorGUILayout.LabelField("UV Channel", "No UV channels available");
            }
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Popup("UV Channel", 0, new[] { "UV0" });
                EditorGUI.EndDisabledGroup();
            }

            // Submesh dropdown
            bool hasSubmeshOptions = _targetMesh != null && _submeshOptions != null && _submeshOptions.Length > 0;
            int displayIndex = hasSubmeshOptions ? _submeshIndex + 1 : 0;
            string[] submeshOptionsToShow = hasSubmeshOptions ? _submeshOptions : new[] { "All" };

            EditorGUI.BeginDisabledGroup(!hasSubmeshOptions || (_targetMesh != null && _targetMesh.subMeshCount <= 1));
            EditorGUI.BeginChangeCheck();
            displayIndex = EditorGUILayout.Popup("Submesh", displayIndex, submeshOptionsToShow);
            if (EditorGUI.EndChangeCheck() && hasSubmeshOptions)
            {
                _submeshIndex = displayIndex - 1;
                UpdateMaterialForSubmesh();
                // Sync material index with submesh
                if (_submeshIndex == -1)
                {
                    // "All" selected - sync to first material
                    _materialIndex = 0;
                    _backgroundMaterial = _sourceMaterials.Length > 0 ? _sourceMaterials[0] : null;
                    _lastSettingsHash = 0;
                }
                else if (_submeshIndex >= 0 && _submeshIndex < _sourceMaterials.Length)
                {
                    _materialIndex = _submeshIndex;
                    _backgroundMaterial = _sourceMaterials[_materialIndex];
                    _lastSettingsHash = 0;
                }
            }
            EditorGUI.EndDisabledGroup();
        }

        private void DrawScrollableSettings()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUIStyle.none, GUI.skin.verticalScrollbar);
            EditorGUILayout.LabelField("", GUILayout.Width(300), GUILayout.Height(0));
            EditorGUILayout.Space(5);

            DrawTileRangeSettings();
            EditorGUILayout.Space(5);
            DrawDrawSettings();

            EditorGUILayout.Space(10);
            EditorGUILayout.EndScrollView();
        }

        private void DrawTileRangeSettings()
        {
            EditorGUILayout.LabelField("Tile Range", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                EditorGUI.BeginChangeCheck();
                _autoDetectTiles = EditorGUILayout.Toggle("Auto Detect", _autoDetectTiles);

                if (EditorGUI.EndChangeCheck())
                {
                    if (!_autoDetectTiles)
                    {
                        _manualMinTile = _detectedMinTile;
                        _manualMaxTile = _detectedMaxTile;
                    }
                }

                EditorGUI.BeginDisabledGroup(_autoDetectTiles);
                EditorGUI.indentLevel++;
                Vector2Int displayMinTile = _autoDetectTiles ? _detectedMinTile : _manualMinTile;
                Vector2Int displayMaxTile = _autoDetectTiles ? _detectedMaxTile : _manualMaxTile;

                int manualMinX = _manualMinTile.x;
                int manualMaxX = _manualMaxTile.x;
                DrawTileRangeSlider("X", ref manualMinX, ref manualMaxX, displayMinTile.x, displayMaxTile.x);

                if (!_autoDetectTiles)
                {
                    _manualMinTile.x = manualMinX;
                    _manualMaxTile.x = manualMaxX;
                }

                int manualMinY = _manualMinTile.y;
                int manualMaxY = _manualMaxTile.y;
                DrawTileRangeSlider("Y", ref manualMinY, ref manualMaxY, displayMinTile.y, displayMaxTile.y);

                if (!_autoDetectTiles)
                {
                    _manualMinTile.y = manualMinY;
                    _manualMaxTile.y = manualMaxY;
                }

                EditorGUI.indentLevel--;
                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawDrawSettings()
        {
            EditorGUILayout.LabelField("Draw Settings", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(GUI.skin.box))
            {
                // Wireframe
                _drawLines = EditorGUILayout.Toggle("Wireframe", _drawLines);
                EditorGUI.BeginDisabledGroup(!_drawLines);
                EditorGUI.indentLevel++;
                _lineColor = EditorGUILayout.ColorField(new GUIContent("Color"), _lineColor, true, true, false);
                EditorGUI.indentLevel--;
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.Space(3);

                // Fill
                _fillTriangles = EditorGUILayout.Toggle("Fill", _fillTriangles);
                EditorGUI.BeginDisabledGroup(!_fillTriangles);
                EditorGUI.indentLevel++;
                _triangleColor = EditorGUILayout.ColorField(new GUIContent("Color"), _triangleColor, true, true, false);
                EditorGUI.indentLevel--;
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.Space(3);

                // Background
                _drawBackground = EditorGUILayout.Toggle("Background", _drawBackground);
                EditorGUI.BeginDisabledGroup(!_drawBackground);
                EditorGUI.indentLevel++;
                _backgroundColor = EditorGUILayout.ColorField(new GUIContent("Color"), _backgroundColor, true, true, false);
                EditorGUI.indentLevel--;
                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawExportSection()
        {
            bool exportSizeExceeded = IsExportSizeExceeded(out int exportWidth, out int exportHeight);

            if (exportSizeExceeded)
            {
                EditorGUILayout.HelpBox(
                    $"Export size ({exportWidth}×{exportHeight}) exceeds maximum ({Constants.MAX_EXPORT_PIXELS}×{Constants.MAX_EXPORT_PIXELS})\n" +
                    "Please reduce tile range or resolution.",
                    MessageType.Error);
            }

            string[] resolutionLabels = { "512", "1024", "2048", "4096", "8192" };
            _resolutionIndex = EditorGUILayout.Popup("Tile Resolution", _resolutionIndex, resolutionLabels);
            EditorGUILayout.Space(10);

            EditorGUI.BeginDisabledGroup(_targetMesh == null || _previewTexture == null || exportSizeExceeded);
            if (GUILayout.Button("Export PNG", GUILayout.Height(50)))
            {
                ExportPNG();
            }
            EditorGUI.EndDisabledGroup();
        }

        private bool ShouldUpdatePreview()
        {
            if (_targetMesh == null) return false;

            int currentHash = GetSettingsHash();
            return currentHash != _lastSettingsHash;
        }

        private void UpdatePreview()
        {
            _lastSettingsHash = GetSettingsHash();
            GeneratePreview();
        }

        private int GetSettingsHash()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (_targetMesh != null ? _targetMesh.GetHashCode() : 0);
                hash = hash * 31 + _drawBackground.GetHashCode();
                hash = hash * 31 + _backgroundColor.GetHashCode();
                hash = hash * 31 + _previewMaterial.GetHashCode();
                hash = hash * 31 + (_backgroundMaterial != null ? _backgroundMaterial.GetHashCode() : 0);
                hash = hash * 31 + _drawLines.GetHashCode();
                hash = hash * 31 + _lineColor.GetHashCode();
                hash = hash * 31 + _fillTriangles.GetHashCode();
                hash = hash * 31 + _triangleColor.GetHashCode();
                hash = hash * 31 + _uvChannel;
                hash = hash * 31 + _submeshIndex;
                hash = hash * 31 + _autoDetectTiles.GetHashCode();
                hash = hash * 31 + _manualMinTile.GetHashCode();
                hash = hash * 31 + _manualMaxTile.GetHashCode();
                return hash;
            }
        }

        private void DrawTileRangeSlider(string label, ref int manualMin, ref int manualMax, int displayMin, int displayMax)
        {
            int oldIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            int newMin = 0;
            int newMax = 0;
            float minFloat = 0;
            float maxFloat = 0;
            bool minFieldChanged = false;
            bool sliderChanged = false;
            bool maxFieldChanged = false;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(oldIndent * 15);
                EditorGUILayout.LabelField(label, GUILayout.Width(134));
                EditorGUI.BeginChangeCheck();
                newMin = EditorGUILayout.IntField(displayMin, GUILayout.Width(30));
                minFieldChanged = EditorGUI.EndChangeCheck();
                GUILayout.Space(5);
                EditorGUI.BeginChangeCheck();
                minFloat = displayMin;
                maxFloat = displayMax;
                EditorGUILayout.MinMaxSlider(ref minFloat, ref maxFloat, Constants.TILE_RANGE_MIN, Constants.TILE_RANGE_MAX);
                sliderChanged = EditorGUI.EndChangeCheck();
                GUILayout.Space(5);
                EditorGUI.BeginChangeCheck();
                newMax = EditorGUILayout.IntField(displayMax, GUILayout.Width(30));
                maxFieldChanged = EditorGUI.EndChangeCheck();
            }
            EditorGUI.indentLevel = oldIndent;

            if (minFieldChanged || maxFieldChanged || sliderChanged)
            {
                int resultMin = displayMin;
                int resultMax = displayMax;

                if (sliderChanged)
                {
                    resultMin = Mathf.RoundToInt(minFloat);
                    resultMax = Mathf.RoundToInt(maxFloat);
                }
                else
                {
                    if (minFieldChanged) resultMin = newMin;
                    if (maxFieldChanged) resultMax = newMax;
                }

                // Swap if min and max are inverted
                if (resultMin > resultMax)
                {
                    (resultMin, resultMax) = (resultMax, resultMin);
                }

                manualMin = resultMin;
                manualMax = resultMax;
            }
        }

        private void ClearMeshCache()
        {
            _cachedUVs = new Vector2[8][];
            _cachedTriangles = null;
            _hasUVChannels = new bool[8];
            _uvChannelOptions = null;
            _uvChannelIndices = null;
            _submeshOptions = null;
            _materialOptions = null;
            _materialIndex = 0;
        }

        private void UpdateMeshCache()
        {
            if (_targetMesh == null)
            {
                ClearMeshCache();
                return;
            }

            // Cache UV channels
            List<Vector2> uvList = new List<Vector2>();
            for (int i = 0; i < 8; i++)
            {
                uvList.Clear();
                _targetMesh.GetUVs(i, uvList);
                _hasUVChannels[i] = uvList.Count > 0;
                if (_hasUVChannels[i])
                {
                    _cachedUVs[i] = uvList.ToArray();
                }
                else
                {
                    _cachedUVs[i] = null;
                }
            }

            // Cache triangles
            _cachedTriangles = _targetMesh.triangles;

            // Cache UI options for UV channels
            List<string> channelList = new List<string>();
            List<int> indexList = new List<int>();
            for (int i = 0; i < 8; i++)
            {
                if (_hasUVChannels[i])
                {
                    channelList.Add($"UV{i}");
                    indexList.Add(i);
                }
            }
            _uvChannelOptions = channelList.ToArray();
            _uvChannelIndices = indexList.ToArray();

            // Cache UI options for submeshes
            UpdateSubmeshOptions();
        }

        private void UpdateSubmeshOptions()
        {
            if (_targetMesh == null)
            {
                _submeshOptions = null;
                return;
            }

            List<string> options = new List<string> { "All" };
            for (int i = 0; i < _targetMesh.subMeshCount; i++)
            {
                options.Add($"#{i}");
            }
            _submeshOptions = options.ToArray();

            // Update material options
            UpdateMaterialOptions();
        }

        private void UpdateMaterialOptions()
        {
            if (_sourceMaterials == null || _sourceMaterials.Length == 0)
            {
                _materialOptions = null;
                _materialIndex = 0;
                _backgroundMaterial = null;
                return;
            }

            List<string> options = new List<string>();
            for (int i = 0; i < _sourceMaterials.Length; i++)
            {
                if (_sourceMaterials[i] != null)
                {
                    options.Add($"[{i}] {_sourceMaterials[i].name}");
                }
                else
                {
                    options.Add($"[{i}] (None)");
                }
            }
            _materialOptions = options.ToArray();

            // Clamp material index
            if (_materialIndex >= _sourceMaterials.Length)
            {
                _materialIndex = 0;
            }

            // Update background material
            if (_materialIndex >= 0 && _materialIndex < _sourceMaterials.Length)
            {
                _backgroundMaterial = _sourceMaterials[_materialIndex];
            }
        }

        private Vector2[] GetUVs(Mesh mesh, int channel)
        {
            if (mesh == null || channel < 0 || channel >= 8) return null;
            return _cachedUVs[channel];
        }

        private bool HasUVChannel(Mesh mesh, int channel)
        {
            if (mesh == null || channel < 0 || channel >= 8) return false;
            return _hasUVChannels[channel];
        }

        private int[] GetTriangles(Mesh mesh, int submeshIndex)
        {
            if (mesh == null) return null;

            if (submeshIndex == -1)
            {
                return _cachedTriangles;
            }

            if (submeshIndex >= 0 && submeshIndex < mesh.subMeshCount)
            {
                return mesh.GetTriangles(submeshIndex);
            }

            return null;
        }

        private void GeneratePreview()
        {
            if (_previewTexture != null)
            {
                DestroyImmediate(_previewTexture);
                _previewTexture = null;
            }

            if (_targetMesh == null) return;

            Vector2[] uvs = GetUVs(_targetMesh, _uvChannel);
            if (uvs == null || uvs.Length == 0) return;

            int[] triangles = GetTriangles(_targetMesh, _submeshIndex);
            if (triangles == null || triangles.Length == 0) return;

            if (_autoDetectTiles)
            {
                DetectTileRange(uvs, triangles);
            }
            else
            {
                _detectedMinTile = _manualMinTile;
                _detectedMaxTile = _manualMaxTile;
            }

            if (IsTileCountExceeded())
            {
                return;
            }

            _previewTexture = GenerateUVTexture(uvs, triangles, Constants.PREVIEW_SIZE, Constants.LINE_THICKNESS, true);
        }

        private void DetectTileRange(Vector2[] uvs, int[] triangles)
        {
            if (uvs == null || uvs.Length == 0 || triangles == null || triangles.Length == 0) return;

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            for (int i = 0; i < triangles.Length; i += 3)
            {
                Vector2 uv0 = uvs[triangles[i]];
                Vector2 uv1 = uvs[triangles[i + 1]];
                Vector2 uv2 = uvs[triangles[i + 2]];

                float area = Mathf.Abs((uv1.x - uv0.x) * (uv2.y - uv0.y) - (uv2.x - uv0.x) * (uv1.y - uv0.y)) * 0.5f;

                if (area < Constants.DEGENERATE_TRIANGLE_AREA_THRESHOLD)
                    continue;

                Vector2[] triUVs = { uv0, uv1, uv2 };
                foreach (var uv in triUVs)
                {
                    if (!IsNearTileBoundary(uv.x))
                    {
                        minX = Mathf.Min(minX, uv.x);
                        maxX = Mathf.Max(maxX, uv.x);
                    }
                    if (!IsNearTileBoundary(uv.y))
                    {
                        minY = Mathf.Min(minY, uv.y);
                        maxY = Mathf.Max(maxY, uv.y);
                    }
                }
            }

            if (minX == float.MaxValue)
            {
                _detectedMinTile = Vector2Int.zero;
                _detectedMaxTile = Vector2Int.zero;
                return;
            }

            _detectedMinTile = new Vector2Int(Mathf.FloorToInt(minX), Mathf.FloorToInt(minY));
            _detectedMaxTile = new Vector2Int(Mathf.FloorToInt(maxX), Mathf.FloorToInt(maxY));
        }

        private bool IsNearTileBoundary(float value)
        {
            float fractionalPart = value - Mathf.Floor(value);
            return fractionalPart < Constants.TILE_BOUNDARY_EPSILON ||
                   fractionalPart > (1.0f - Constants.TILE_BOUNDARY_EPSILON);
        }

        private bool IsTileCountExceeded()
        {
            Vector2Int minTile = _autoDetectTiles ? _detectedMinTile : _manualMinTile;
            Vector2Int maxTile = _autoDetectTiles ? _detectedMaxTile : _manualMaxTile;
            int tilesX = maxTile.x - minTile.x + 1;
            int tilesY = maxTile.y - minTile.y + 1;
            int totalTiles = tilesX * tilesY;
            return totalTiles > Constants.MAX_PREVIEW_TILES;
        }

        private bool IsExportSizeExceeded(out int exportWidth, out int exportHeight)
        {
            int resolution = _resolutionOptions[_resolutionIndex];
            Vector2Int minTile = _autoDetectTiles ? _detectedMinTile : _manualMinTile;
            Vector2Int maxTile = _autoDetectTiles ? _detectedMaxTile : _manualMaxTile;
            int tilesX = maxTile.x - minTile.x + 1;
            int tilesY = maxTile.y - minTile.y + 1;

            exportWidth = resolution * tilesX;
            exportHeight = resolution * tilesY;

            return exportWidth > Constants.MAX_EXPORT_PIXELS || exportHeight > Constants.MAX_EXPORT_PIXELS;
        }

        private Texture2D GenerateUVTexture(Vector2[] uvs, int[] triangles, int resolution, float lineThickness, bool isPreview = false)
        {
            Vector2Int minTile = _autoDetectTiles ? _detectedMinTile : _manualMinTile;
            Vector2Int maxTile = _autoDetectTiles ? _detectedMaxTile : _manualMaxTile;

            int tilesX = maxTile.x - minTile.x + 1;
            int tilesY = maxTile.y - minTile.y + 1;

            int texWidth = resolution * tilesX;
            int texHeight = resolution * tilesY;

            Texture2D texture = new Texture2D(texWidth, texHeight, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[texWidth * texHeight];

            // Check if we should show material preview (only when GameObject is selected)
            if (isPreview && _previewMaterial && _backgroundMaterial != null && _sourceGameObject != null)
            {
                // Draw material preview with checkerboard for alpha if Background is off
                DrawMaterialBackgroundToPixels(pixels, texWidth, texHeight, tilesX, tilesY, minTile, maxTile, isPreview, !_drawBackground);

                // If Background is on, blend background color over the material preview
                if (_drawBackground)
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = BlendColors(pixels[i], _backgroundColor);
                    }
                }
            }
            else if (!_drawBackground)
            {
                // No material preview, Background off: show checkerboard or transparent
                if (isPreview)
                {
                    DrawTransparencyGridToPixels(pixels, texWidth, texHeight, tilesX, tilesY);
                }
                else
                {
                    Color transparentColor = new Color(_lineColor.r, _lineColor.g, _lineColor.b, 0);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = transparentColor;
                    }
                }
            }
            else
            {
                // No material preview, Background on: solid color background
                if (isPreview && _backgroundColor.a < 1f)
                {
                    // Blend background color over transparency grid if background has alpha
                    DrawTransparencyGridToPixels(pixels, texWidth, texHeight, tilesX, tilesY);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = BlendColors(pixels[i], _backgroundColor);
                    }
                }
                else
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = _backgroundColor;
                    }
                }
            }

            Vector2 offset = new Vector2(minTile.x, minTile.y);
            Vector2 scale = new Vector2(tilesX, tilesY);

            if (_fillTriangles)
            {
                // Pre-compute pixels for triangle fill
                HashSet<int> fillPixels = new HashSet<int>();

                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector2 uv0 = (uvs[triangles[i]] - offset) / scale;
                    Vector2 uv1 = (uvs[triangles[i + 1]] - offset) / scale;
                    Vector2 uv2 = (uvs[triangles[i + 2]] - offset) / scale;

                    CollectTrianglePixels(fillPixels, texWidth, texHeight, uv0, uv1, uv2);
                }

                // Batch fill the computed pixels
                foreach (var index in fillPixels)
                {
                    if (index >= 0 && index < pixels.Length)
                    {
                        pixels[index] = BlendColors(pixels[index], _triangleColor);
                    }
                }
            }

            if (_drawLines)
            {
                // Pre-compute pixels for line drawing
                HashSet<int> linePixels = new HashSet<int>();
                HashSet<(int, int)> drawnEdges = new HashSet<(int, int)>();

                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int idx0 = triangles[i];
                    int idx1 = triangles[i + 1];
                    int idx2 = triangles[i + 2];

                    Vector2 uv0 = (uvs[idx0] - offset) / scale;
                    Vector2 uv1 = (uvs[idx1] - offset) / scale;
                    Vector2 uv2 = (uvs[idx2] - offset) / scale;

                    var edge01 = idx0 < idx1 ? (idx0, idx1) : (idx1, idx0);
                    if (!drawnEdges.Contains(edge01))
                    {
                        CollectLinePixels(linePixels, texWidth, texHeight, uv0, uv1, lineThickness);
                        drawnEdges.Add(edge01);
                    }

                    var edge12 = idx1 < idx2 ? (idx1, idx2) : (idx2, idx1);
                    if (!drawnEdges.Contains(edge12))
                    {
                        CollectLinePixels(linePixels, texWidth, texHeight, uv1, uv2, lineThickness);
                        drawnEdges.Add(edge12);
                    }

                    var edge20 = idx2 < idx0 ? (idx2, idx0) : (idx0, idx2);
                    if (!drawnEdges.Contains(edge20))
                    {
                        CollectLinePixels(linePixels, texWidth, texHeight, uv2, uv0, lineThickness);
                        drawnEdges.Add(edge20);
                    }
                }

                // Batch fill the computed pixels
                foreach (var index in linePixels)
                {
                    if (index >= 0 && index < pixels.Length)
                    {
                        pixels[index] = BlendColors(pixels[index], _lineColor);
                    }
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();

            return texture;
        }

        private void CollectLinePixels(HashSet<int> linePixels, int width, int height, Vector2 start, Vector2 end, float thickness)
        {
            Vector2Int p0 = new Vector2Int(
                Mathf.RoundToInt(start.x * width),
                Mathf.RoundToInt(start.y * height)
            );
            Vector2Int p1 = new Vector2Int(
                Mathf.RoundToInt(end.x * width),
                Mathf.RoundToInt(end.y * height)
            );

            int dx = Mathf.Abs(p1.x - p0.x);
            int dy = Mathf.Abs(p1.y - p0.y);
            int sx = p0.x < p1.x ? 1 : -1;
            int sy = p0.y < p1.y ? 1 : -1;
            int err = dx - dy;

            int x = p0.x;
            int y = p0.y;

            int steps = 0;

            while (steps < 10000)
            {
                CollectThickPixel(linePixels, width, height, x, y, thickness);

                if (x == p1.x && y == p1.y) break;

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }

                steps++;
            }
        }

        private void CollectThickPixel(HashSet<int> linePixels, int width, int height, int centerX, int centerY, float thickness)
        {
            int radius = Mathf.CeilToInt(thickness / 2f);
            float radiusSq = (thickness / 2f) * (thickness / 2f);

            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int px = centerX + dx;
                    int py = centerY + dy;

                    if (px < 0 || px >= width || py < 0 || py >= height)
                        continue;

                    float distanceSq = dx * dx + dy * dy;

                    if (distanceSq <= radiusSq)
                    {
                        int index = py * width + px;
                        linePixels.Add(index);
                    }
                }
            }
        }

        private void ExportPNG()
        {
            if (_targetMesh == null)
            {
                EditorUtility.DisplayDialog("Error", "No mesh selected", "OK");
                return;
            }

            Vector2[] uvs = GetUVs(_targetMesh, _uvChannel);
            if (uvs == null || uvs.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "No UVs found in selected channel", "OK");
                return;
            }

            int[] triangles = GetTriangles(_targetMesh, _submeshIndex);
            if (triangles == null || triangles.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "No triangles found", "OK");
                return;
            }

            if (IsExportSizeExceeded(out int exportWidth, out int exportHeight))
            {
                EditorUtility.DisplayDialog("Export Size Error",
                    $"Export size ({exportWidth}×{exportHeight}) exceeds maximum ({Constants.MAX_EXPORT_PIXELS}×{Constants.MAX_EXPORT_PIXELS})\n" +
                    "Please reduce tile range or resolution.",
                    "OK");
                return;
            }

            int resolution = _resolutionOptions[_resolutionIndex];
            Texture2D exportTexture = GenerateUVTexture(uvs, triangles, resolution, Constants.LINE_THICKNESS, false);

            string meshName = _targetMesh.name;
            string submeshSuffix = _submeshIndex == -1 ? "All" : $"Sub{_submeshIndex}";
            string defaultName = $"{meshName}_UV{_uvChannel}_{submeshSuffix}_{resolution}.png";
            string path = EditorUtility.SaveFilePanel(
                "Save UV Map as PNG",
                _lastSavedPath,
                defaultName,
                "png"
            );

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                byte[] pngData = exportTexture.EncodeToPNG();
                System.IO.File.WriteAllBytes(path, pngData);

                _lastSavedPath = System.IO.Path.GetDirectoryName(path);

                if (path.StartsWith(Application.dataPath))
                {
                    string assetPath = "Assets" + path.Substring(Application.dataPath.Length);
                    AssetDatabase.Refresh();

                    TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                    if (importer != null)
                    {
                        importer.textureType = TextureImporterType.Default;
                        importer.sRGBTexture = true;
                        importer.isReadable = true;
                        AssetDatabase.ImportAsset(assetPath);
                    }
                }

                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("Error", $"Failed to export PNG:\n{e.Message}", "OK");
            }
            finally
            {
                DestroyImmediate(exportTexture);
            }
        }

        private void UpdateMaterialForSubmesh()
        {
            if (_sourceMaterials == null || _sourceMaterials.Length == 0)
                return;

            Material mat = null;

            if (_submeshIndex == -1)
            {
                // "All" selected - use first material
                mat = _sourceMaterials[0];
            }
            else if (_submeshIndex >= 0 && _submeshIndex < _sourceMaterials.Length)
            {
                // Specific submesh selected - use corresponding material
                mat = _sourceMaterials[_submeshIndex];
            }

            if (mat != null)
            {
                _backgroundMaterial = mat;
            }
        }

        private void OnDestroy()
        {
            if (_previewTexture != null)
            {
                DestroyImmediate(_previewTexture);
            }
        }

        private void DrawTransparencyGridToPixels(Color[] pixels, int width, int height, int tilesX, int tilesY)
        {
            int maxTiles = Mathf.Max(tilesX, tilesY);
            int gridSize = maxTiles <= 1 ? Constants.DEFAULT_GRID_SIZE : Mathf.Max(Constants.MIN_GRID_SIZE, Constants.DEFAULT_GRID_SIZE / maxTiles);

            Color lightColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            Color darkColor = new Color(0.75f, 0.75f, 0.75f, 1f);

            for (int y = 0; y < height; y++)
            {
                int gridY = y / gridSize;
                for (int x = 0; x < width; x++)
                {
                    int gridX = x / gridSize;
                    Color cellColor = ((gridX + gridY) & 1) == 0 ? lightColor : darkColor;

                    int index = y * width + x;
                    pixels[index] = cellColor;
                }
            }
        }

        private void CollectTrianglePixels(HashSet<int> fillPixels, int width, int height, Vector2 uv0, Vector2 uv1, Vector2 uv2)
        {
            Vector2Int p0 = new Vector2Int(Mathf.RoundToInt(uv0.x * width), Mathf.RoundToInt(uv0.y * height));
            Vector2Int p1 = new Vector2Int(Mathf.RoundToInt(uv1.x * width), Mathf.RoundToInt(uv1.y * height));
            Vector2Int p2 = new Vector2Int(Mathf.RoundToInt(uv2.x * width), Mathf.RoundToInt(uv2.y * height));

            int minX = Mathf.Max(0, Mathf.Min(p0.x, p1.x, p2.x));
            int maxX = Mathf.Min(width - 1, Mathf.Max(p0.x, p1.x, p2.x));
            int minY = Mathf.Max(0, Mathf.Min(p0.y, p1.y, p2.y));
            int maxY = Mathf.Min(height - 1, Mathf.Max(p0.y, p1.y, p2.y));

            // Early return for empty or invalid bounding box
            if (minX > maxX || minY > maxY) return;

            // Pre-calculate edge function coefficients for incremental computation
            // Edge 0: p1 -> p2
            int dy12 = p2.y - p1.y;
            int dx12 = p2.x - p1.x;
            // Edge 1: p2 -> p0
            int dy20 = p0.y - p2.y;
            int dx20 = p0.x - p2.x;
            // Edge 2: p0 -> p1
            int dy01 = p1.y - p0.y;
            int dx01 = p1.x - p0.x;

            // Calculate initial edge values for top-left corner (minX, minY)
            float w0_row = (minX - p1.x) * dy12 - (minY - p1.y) * dx12;
            float w1_row = (minX - p2.x) * dy20 - (minY - p2.y) * dx20;
            float w2_row = (minX - p0.x) * dy01 - (minY - p0.y) * dx01;

            // Rasterize triangle using incremental edge function updates
            for (int y = minY; y <= maxY; y++)
            {
                float w0 = w0_row;
                float w1 = w1_row;
                float w2 = w2_row;

                for (int x = minX; x <= maxX; x++)
                {
                    // Support both CCW and CW winding orders
                    if ((w0 >= 0 && w1 >= 0 && w2 >= 0) || (w0 <= 0 && w1 <= 0 && w2 <= 0))
                    {
                        fillPixels.Add(y * width + x);
                    }

                    // Increment edge values for next pixel in row (x direction)
                    w0 += dy12;
                    w1 += dy20;
                    w2 += dy01;
                }

                // Move to next row (y direction)
                w0_row -= dx12;
                w1_row -= dx20;
                w2_row -= dx01;
            }
        }

        private Color BlendColors(Color background, Color foreground)
        {
            float alpha = foreground.a;

            if (alpha >= 1f)
            {
                return foreground;
            }

            if (alpha <= 0f)
            {
                return background;
            }

            float invAlpha = 1f - alpha;

            return new Color(
                foreground.r * alpha + background.r * invAlpha,
                foreground.g * alpha + background.g * invAlpha,
                foreground.b * alpha + background.b * invAlpha,
                background.a + alpha * (1f - background.a)
            );
        }

        private void DrawMaterialBackgroundToPixels(Color[] pixels, int width, int height, int tilesX, int tilesY, Vector2Int minTile, Vector2Int maxTile, bool isPreview, bool showCheckerboardForAlpha = false)
        {
            if (_backgroundMaterial == null)
            {
                // Fallback to checkerboard if no material
                if (isPreview)
                {
                    DrawTransparencyGridToPixels(pixels, width, height, tilesX, tilesY);
                }
                else
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = Color.white;
                    }
                }
                return;
            }

            // Render material covering the full tile range with proper UV offset
            int renderResolution = 512;
            Texture2D renderedTexture = RenderMaterialToTexture(_backgroundMaterial, renderResolution, renderResolution, minTile, maxTile);

            if (renderedTexture == null)
            {
                // Fallback to checkerboard if rendering failed
                if (isPreview)
                {
                    DrawTransparencyGridToPixels(pixels, width, height, tilesX, tilesY);
                }
                else
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = Color.white;
                    }
                }
                return;
            }

            // If showCheckerboardForAlpha is true, first draw checkerboard pattern
            if (showCheckerboardForAlpha)
            {
                DrawTransparencyGridToPixels(pixels, width, height, tilesX, tilesY);
            }

            // Get all pixels at once for better performance
            Color[] renderedPixels = renderedTexture.GetPixels();

            // Copy rendered texture to pixels, blending over checkerboard if needed
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Direct sampling from rendered texture
                    int texX = Mathf.RoundToInt((float)x / width * (renderResolution - 1));
                    int texY = Mathf.RoundToInt((float)y / height * (renderResolution - 1));

                    texX = Mathf.Clamp(texX, 0, renderResolution - 1);
                    texY = Mathf.Clamp(texY, 0, renderResolution - 1);

                    int index = y * width + x;
                    int texIndex = texY * renderResolution + texX;
                    Color materialColor = renderedPixels[texIndex];

                    if (showCheckerboardForAlpha && materialColor.a < 1f)
                    {
                        // Blend material color over checkerboard
                        pixels[index] = BlendColors(pixels[index], materialColor);
                    }
                    else
                    {
                        pixels[index] = materialColor;
                    }
                }
            }

            DestroyImmediate(renderedTexture);
        }

        private Texture2D RenderMaterialToTexture(Material material, int resolution, int resolution2, Vector2Int minTile, Vector2Int maxTile)
        {
            if (material == null) return null;

            // Create a dedicated preview scene
            Scene previewScene = EditorSceneManager.NewPreviewScene();
            GameObject tempCameraObj = null;
            GameObject tempQuadObj = null;
            GameObject tempLightObj = null;
            Texture2D result = null;

            try
            {
                // Create camera object
                tempCameraObj = new GameObject("PreviewCamera");
                SceneManager.MoveGameObjectToScene(tempCameraObj, previewScene);

                Camera tempCamera = tempCameraObj.AddComponent<Camera>();
                tempCamera.orthographic = true;
                tempCamera.orthographicSize = 0.5f;
                tempCamera.nearClipPlane = 0.1f;
                tempCamera.farClipPlane = 2f;
                tempCamera.backgroundColor = Color.clear;
                tempCamera.clearFlags = CameraClearFlags.SolidColor;
                tempCamera.enabled = false;
                tempCamera.scene = previewScene;

                // Position camera
                tempCameraObj.transform.position = new Vector3(0, 0, -1f);
                tempCameraObj.transform.rotation = Quaternion.identity;

                // Create directional light for consistent lighting
                tempLightObj = new GameObject("PreviewLight");
                SceneManager.MoveGameObjectToScene(tempLightObj, previewScene);

                Light light = tempLightObj.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.0f;
                light.color = Color.white;
                tempLightObj.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                // Create quad with UVs adjusted for tile range
                tempQuadObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
                SceneManager.MoveGameObjectToScene(tempQuadObj, previewScene);

                tempQuadObj.transform.position = new Vector3(0, 0, 0);
                tempQuadObj.transform.rotation = Quaternion.identity;
                tempQuadObj.transform.localScale = Vector3.one;

                // Adjust UVs to cover the tile range
                MeshFilter meshFilter = tempQuadObj.GetComponent<MeshFilter>();
                Mesh mesh = meshFilter.sharedMesh;
                Mesh tempMesh = new Mesh();
                try
                {
                    tempMesh.vertices = mesh.vertices;
                    tempMesh.triangles = mesh.triangles;
                    tempMesh.normals = mesh.normals;

                    // Calculate UV range based on tiles
                    int tilesX = maxTile.x - minTile.x + 1;
                    int tilesY = maxTile.y - minTile.y + 1;

                    Vector2[] uvs = mesh.uv;
                    for (int i = 0; i < uvs.Length; i++)
                    {
                        // Scale UV to cover all tiles and offset by minTile
                        uvs[i] = new Vector2(
                            uvs[i].x * tilesX + minTile.x,
                            uvs[i].y * tilesY + minTile.y
                        );
                    }
                    tempMesh.uv = uvs;
                    meshFilter.sharedMesh = tempMesh;

                    // Apply material
                    MeshRenderer renderer = tempQuadObj.GetComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;

                    // Create render texture
                    RenderTexture renderTexture = RenderTexture.GetTemporary(
                        resolution,
                        resolution2,
                        24,
                        RenderTextureFormat.ARGB32,
                        RenderTextureReadWrite.sRGB);

                    renderTexture.wrapMode = TextureWrapMode.Clamp;

                    tempCamera.targetTexture = renderTexture;

                    // Render
                    tempCamera.Render();

                    // Read pixels from render texture
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = renderTexture;

                    result = new Texture2D(resolution, resolution2, TextureFormat.RGBA32, false);
                    result.ReadPixels(new Rect(0, 0, resolution, resolution2), 0, 0);
                    result.Apply();

                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(renderTexture);
                }
                finally
                {
                    // Destroy temporary mesh to prevent memory leak
                    if (tempMesh != null)
                    {
                        DestroyImmediate(tempMesh);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"Failed to render material: {e.Message}");
            }
            finally
            {
                // Clean up preview scene and all objects in it
                if (previewScene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(previewScene);
                }
            }

            return result;
        }
    }
}
#endif
