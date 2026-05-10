/*  Copyright (C) 2012 Ian Brown

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

using HelixToolkit.Wpf;
using JetBlackEngineLib.Data.Animation;
using JetBlackEngineLib.Data.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using WorldExplorer.DataExporters;
using WorldExplorer.Win3D;
using MessageBox = System.Windows.MessageBox;

namespace WorldExplorer;

public class ModelViewModel : BaseViewModel
{
    private readonly ModelView _modelView;
    private AnimData? _animData;

    private Camera _camera = new OrthographicCamera
    {
        Position = new Point3D(0, 10, -10), LookDirection = new Vector3D(0, -1, 1)
    };

    private Transform3D _cameraTransform = Transform3D.Identity;

    private int _currentFrame;

    private string? _infoText;

    private ModelVisual3D? _model;

    private Model? _vifModel;

    // Set by SetCompositeModel for DDF entities that bundle multiple
    // (mesh, texture) pairs. When non-null, UpdateModel rebuilds the visual
    // as one ModelVisual3D per part so each keeps its own texture across
    // animation frames and the normals toggle. Cleared by the VifModel
    // setter (single-mesh path).
    private IList<(Model vif, BitmapSource? texture)>? _parts;

    private readonly System.Windows.Threading.DispatcherTimer _playTimer = new()
        { Interval = TimeSpan.FromMilliseconds(16.66f) };
    private bool _isPlaying;

    public AnimData? AnimData
    {
        get => _animData;
        set
        {
            _animData = value;
            // Stop playback whenever the clip changes — otherwise the timer
            // would keep advancing into the new clip's frame range from a
            // stale index.
            IsPlaying = false;
            CurrentFrame = 0;
            UpdateModel(false);
            OnPropertyChanged(nameof(AnimData));
            OnPropertyChanged(nameof(MaximumFrame));
        }
    }

    /// <summary>Animation playback state. Setting it starts/stops the tick timer.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            if (_isPlaying) _playTimer.Start(); else _playTimer.Stop();
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(PlayButtonLabel));
        }
    }

    public string PlayButtonLabel => _isPlaying ? "Pause" : "Play";

    public void TogglePlay() => IsPlaying = !_isPlaying;

    public WriteableBitmap? Texture { get; set; }

    public Model? VifModel
    {
        get => _vifModel;
        set
        {
            _vifModel = value;
            // Single-mesh assignment drops any prior composite parts; otherwise
            // UpdateModel would still rebuild from stale parts.
            _parts = null;
            UpdateModel(true);
            OnPropertyChanged(nameof(VifModel));
        }
    }

    /// <summary>Force a rebuild of the visual without altering VifModel/parts.
    /// Used by the normals checkbox so toggling it preserves per-part textures
    /// of a composite DDF entity.</summary>
    public void RefreshModel() => UpdateModel(false);

    public int MaximumFrame
    {
        get => _animData == null ? 0 : _animData.NumFrames - 1;
        // set { }
    }

    public int CurrentFrame
    {
        get => _currentFrame;
        set
        {
            _currentFrame = value;
            UpdateModel(false);
            OnPropertyChanged(nameof(CurrentFrame));
        }
    }

    public string?InfoText
    {
        get => _infoText;
        set
        {
            _infoText = value;
            OnPropertyChanged(nameof(InfoText));
        }
    }

    public ModelVisual3D? Model
    {
        get => _model;
        set
        {
            _model = value;

            // Always remove the previous container; only add the new one if non-null.
            // Without the unconditional remove, clearing the selection (Model = null)
            // leaves the previous mesh on screen.
            _modelView.viewport.Children.Remove(_modelView.modelObject);
            if (_model != null)
            {
                // Composite containers (multi-mesh entities) have Content == null
                // and use Children instead — union the child bounds in that case.
                var bounds = _model.Content?.Bounds;
                if (bounds == null)
                {
                    var rect = Rect3D.Empty;
                    foreach (var child in _model.Children)
                    {
                        if (child is ModelVisual3D mv && mv.Content != null)
                            rect.Union(mv.Content.Bounds);
                    }
                    bounds = rect;
                }
                InfoText = $"Model Bounds: {bounds}";
                _modelView.modelObject = _model;
                _modelView.viewport.Children.Add(_modelView.modelObject);
            }
            else
            {
                InfoText = null;
                _modelView.modelObject = new ModelVisual3D();
            }

            OnPropertyChanged(nameof(Model));
        }
    }

    public Transform3D CameraTransform
    {
        get => _cameraTransform;
        set
        {
            _cameraTransform = value;
            _camera.Transform = _cameraTransform;
            OnPropertyChanged(nameof(CameraTransform));
        }
    }

    public Camera Camera
    {
        get => _camera;
        set
        {
            _camera = value;
            OnPropertyChanged(nameof(Camera));
        }
    }

    public ModelViewModel(MainWindowViewModel mainViewWindow) : base(mainViewWindow)
    {
        _modelView = MainViewModel.MainWindow.modelView;
        _playTimer.Tick += (_, _) =>
        {
            var max = MaximumFrame;
            if (max <= 0) { IsPlaying = false; return; }
            CurrentFrame = (CurrentFrame + 1) % (max + 1);
        };
    }

    /// <summary>
    /// Render multiple meshes as a single composite model — used for DDF
    /// entities (cat 0 in particular) where one entity bundles several
    /// (mesh, texture) pairs (e.g. body + hair). Each part keeps its own
    /// texture material instead of being merged into one geometry. The
    /// underlying VifModel is set to the union of all mesh lists so camera
    /// framing and the export hooks see the whole composite.
    /// </summary>
    public void SetCompositeModel(IList<(Model vif, BitmapSource? texture)> parts)
    {
        // Reset animation state without invoking the AnimData setter — that
        // would call UpdateModel before _parts is in place and emit an empty
        // single-mesh frame.
        _animData = null;
        _currentFrame = 0;

        if (parts.Count == 0)
        {
            _parts = null;
            VifModel = null;
            OnPropertyChanged(nameof(AnimData));
            OnPropertyChanged(nameof(MaximumFrame));
            return;
        }

        _parts = parts;
        // Combined VifModel for bone counting (animation match check) and
        // ShowExportForPosedModel.
        var allMeshes = parts.SelectMany(p => p.vif.MeshList).ToList();
        _vifModel = new Model(allMeshes);

        UpdateModel(false);
        OnPropertyChanged(nameof(VifModel));
        OnPropertyChanged(nameof(AnimData));
        OnPropertyChanged(nameof(MaximumFrame));
    }

    private void UpdateModel(bool updateCamera)
    {
        if (_vifModel == null)
        {
            Model = null;
            return;
        }

        var showNormals = _modelView.normalsBox.IsChecked.GetValueOrDefault();
        ModelVisual3D container = new();

        if (_parts != null)
        {
            // Composite (DDF entity) — one GeometryModel3D per part keeps each
            // part's paired texture; any current pose/frame is applied per
            // part so animations work on the entity preview model.
            foreach (var (vif, tex) in _parts)
            {
                var partGeom = (GeometryModel3D)Conversions.CreateModel3D(
                    vif.MeshList, tex, _animData, CurrentFrame);
                container.Children.Add(new ModelVisual3D { Content = partGeom });
                if (showNormals)
                {
                    container.Children.Add(new MeshNormalsVisual3D
                        { Mesh = (MeshGeometry3D)partGeom.Geometry });
                }
            }
        }
        else
        {
            var newModel = (GeometryModel3D)Conversions.CreateModel3D(
                _vifModel.MeshList, Texture, _animData, CurrentFrame);
            container.Content = newModel;
            if (showNormals)
            {
                container.Children.Add(new MeshNormalsVisual3D
                    { Mesh = (MeshGeometry3D)newModel.Geometry });
            }
        }

        Model = container;

        if (updateCamera && _model != null && _model.Content != null)
        {
            UpdateCamera(_model);
        }
    }

    private void UpdateCamera(ModelVisual3D model)
    {
        var oCam = (OrthographicCamera)_camera;

        var bounds = model.Content.Bounds;
        Point3D centroid = new(0, 0, 0);
        var radius =
            Math.Sqrt((bounds.SizeX * bounds.SizeX) + (bounds.SizeY * bounds.SizeY) +
                      (bounds.SizeZ * bounds.SizeZ)) /
            2.0;
        var cameraDistance = radius * 3.0;

        Point3D camPos = new(centroid.X, centroid.Y - cameraDistance, centroid.Z);
        oCam.Position = camPos;
        oCam.Width = cameraDistance;
        oCam.LookDirection = new Vector3D(0, 1, 0);
        oCam.UpDirection = new Vector3D(0, 0, 1);
    }

    public void ShowExportForPosedModel()
    {
        if (Model == null || VifModel == null)
        {
            MessageBox.Show("No model currently loaded.", "Error", MessageBoxButton.OK);
            return;
        }

        SaveFileDialog dialog = new()
        {
            Filter = "GLTF File|*.gltf|OBJ File|*.obj",
            // Select gltf by default
            FilterIndex = 1,
            FileName = "some-model.gltf"
        };
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        var ext = Path.GetExtension(dialog.FileName).ToUpperInvariant();
        IVifExporter? exporter = ext switch
        {
            ".OBJ" => new VifObjExporter(),
            ".GLTF" => new VifGltfExporter(),
            _ => null
        };
        if (exporter == null)
        {
            MessageBox.Show("Unknown file format.", "Error", MessageBoxButton.OK);
            return;
        }

        exporter.SaveToFile(dialog.FileName, VifModel, Texture, AnimData, CurrentFrame);
    }
}