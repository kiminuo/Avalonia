﻿// Copyright (c) The Avalonia Project. All rights reserved.
// Licensed under the MIT license. See licence.md file in the project root for full license information.

using System;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Avalonia.Rendering.SceneGraph
{
    public class SceneBuilder : ISceneBuilder
    {
        public void UpdateAll(Scene scene)
        {
            Contract.Requires<ArgumentNullException>(scene != null);
            Dispatcher.UIThread.VerifyAccess();

            UpdateSize(scene);
            scene.Layers.GetOrAdd(scene.Root.Visual);

            using (var impl = new DeferredDrawingContextImpl(this, scene.Layers))
            using (var context = new DrawingContext(impl))
            {
                Update(context, scene, (VisualNode)scene.Root, scene.Root.Visual.Bounds, true);
            }
        }

        public bool Update(Scene scene, IVisual visual)
        {
            Contract.Requires<ArgumentNullException>(scene != null);
            Contract.Requires<ArgumentNullException>(visual != null);
            Dispatcher.UIThread.VerifyAccess();

            var node = (VisualNode)scene.FindNode(visual);

            if (visual == scene.Root.Visual)
            {
                UpdateSize(scene);
            }

            if (visual.VisualRoot != null)
            {
                if (visual.IsVisible)
                {
                    // If the node isn't yet part of the scene, find the nearest ancestor that is.
                    node = node ?? FindExistingAncestor(scene, visual);

                    // We don't need to do anything if this part of the tree has already been fully
                    // updated.
                    if (node != null && !node.SubTreeUpdated)
                    {
                        // If the control we've been asked to update isn't part of the scene then
                        // we're carrying out an add operation, so recurse and add all the
                        // descendents too.
                        var recurse = node.Visual != visual;

                        using (var impl = new DeferredDrawingContextImpl(this, scene.Layers))
                        using (var context = new DrawingContext(impl))
                        {
                            var clip = scene.Root.Visual.Bounds;

                            if (node.Parent != null)
                            {
                                context.PushPostTransform(node.Parent.Transform);
                                clip = node.Parent.ClipBounds;
                            }

                            using (context.PushTransformContainer())
                            {
                                Update(context, scene, node, clip, recurse);
                            }
                        }

                        return true;
                    }
                }
                else
                {
                    if (node != null)
                    {
                        // The control has been hidden so remove it from its parent and deindex the
                        // node and its descendents.
                        ((VisualNode)node.Parent)?.RemoveChild(node);
                        Deindex(scene, node);
                        return true;
                    }
                }
            }
            else if (node != null)
            {
                // The control has been removed so remove it from its parent and deindex the
                // node and its descendents.
                var trim = FindFirstDeadAncestor(scene, node);
                ((VisualNode)trim.Parent).RemoveChild(trim);
                Deindex(scene, trim);
                return true;
            }

            return false;
        }

        private static VisualNode FindExistingAncestor(Scene scene, IVisual visual)
        {
            var node = scene.FindNode(visual);

            while (node == null && visual.IsVisible)
            {
                visual = visual.VisualParent;
                node = scene.FindNode(visual);
            }

            return visual.IsVisible ? (VisualNode)node : null;
        }

        private static VisualNode FindFirstDeadAncestor(Scene scene, IVisualNode node)
        {
            var parent = node.Parent;

            while (parent.Visual.VisualRoot == null)
            {
                node = parent;
                parent = node.Parent;
            }

            return (VisualNode)node;
        }

        private static void Update(DrawingContext context, Scene scene, VisualNode node, Rect clip, bool forceRecurse)
        {
            var visual = node.Visual;
            var opacity = visual.Opacity;
            var clipToBounds = visual.ClipToBounds;
            var bounds = new Rect(visual.Bounds.Size);
            var contextImpl = (DeferredDrawingContextImpl)context.PlatformImpl;

            contextImpl.Layers.Find(node.LayerRoot)?.Dirty.Add(node.Bounds);

            if (visual.IsVisible)
            {
                var m = Matrix.CreateTranslation(visual.Bounds.Position);

                var renderTransform = Matrix.Identity;

                if (visual.RenderTransform != null)
                {
                    var origin = visual.RenderTransformOrigin.ToPixels(new Size(visual.Bounds.Width, visual.Bounds.Height));
                    var offset = Matrix.CreateTranslation(origin);
                    renderTransform = (-offset) * visual.RenderTransform.Value * (offset);
                }

                m = renderTransform * m;

                using (contextImpl.BeginUpdate(node))
                using (context.PushPostTransform(m))
                using (context.PushTransformContainer())
                {
                    var startLayer = opacity < 1 || visual.OpacityMask != null;

                    forceRecurse = forceRecurse || node.Transform != contextImpl.Transform;

                    node.Transform = contextImpl.Transform;
                    node.ClipBounds = bounds.TransformToAABB(node.Transform).Intersect(clip);
                    node.ClipToBounds = clipToBounds;
                    node.GeometryClip = visual.Clip;
                    node.Opacity = opacity;
                    node.OpacityMask = visual.OpacityMask;

                    if (startLayer)
                    {
                        if (node.LayerRoot != visual)
                        {
                            MakeLayer(scene, node);
                        }
                        else
                        {
                            UpdateLayer(node, scene.Layers[node.LayerRoot]);
                        }
                    }
                    else if (!startLayer && node.LayerRoot == node.Visual && node.Parent != null)
                    {
                        ClearLayer(scene, node);
                    }

                    if (node.ClipToBounds)
                    {
                        clip = clip.Intersect(node.ClipBounds);
                    }

                    try
                    {
                        visual.Render(context);
                    }
                    catch { }

                    if (forceRecurse)
                    {
                        foreach (var child in visual.VisualChildren.OrderBy(x => x, ZIndexComparer.Instance))
                        {
                            var childNode = scene.FindNode(child) ?? CreateNode(scene, child, node);
                            Update(context, scene, (VisualNode)childNode, clip, forceRecurse);
                        }

                        node.SubTreeUpdated = true;
                        contextImpl.TrimChildren();
                    }
                }
            }
        }

        private void UpdateSize(Scene scene)
        {
            var renderRoot = scene.Root.Visual as IRenderRoot;
            var newSize = renderRoot?.ClientSize ?? scene.Root.Visual.Bounds.Size;

            scene.Scaling = renderRoot?.RenderScaling ?? 1;

            if (scene.Size != newSize)
            {
                var oldSize = scene.Size;

                scene.Size = newSize;

                Rect horizontalDirtyRect = Rect.Empty;
                Rect verticalDirtyRect = Rect.Empty;

                if (newSize.Width > oldSize.Width)
                {
                    horizontalDirtyRect = new Rect(oldSize.Width, 0, newSize.Width - oldSize.Width, oldSize.Height);
                }

                if (newSize.Height > oldSize.Height)
                {
                    verticalDirtyRect = new Rect(0, oldSize.Height, newSize.Width, newSize.Height - oldSize.Height);
                }

                foreach (var layer in scene.Layers)
                {
                    layer.Dirty.Add(horizontalDirtyRect);
                    layer.Dirty.Add(verticalDirtyRect);
                }
            }
        }

        private static VisualNode CreateNode(Scene scene, IVisual visual, VisualNode parent)
        {
            var node = new VisualNode(visual, parent);
            node.LayerRoot = parent.LayerRoot;
            scene.Add(node);
            return node;
        }

        private static void Deindex(Scene scene, VisualNode node)
        {
            scene.Remove(node);
            node.SubTreeUpdated = true;

            foreach (VisualNode child in node.Children)
            {
                var geometry = child as IDrawOperation;
                var visual = child as VisualNode;

                if (geometry != null)
                {
                    scene.Layers[child.LayerRoot].Dirty.Add(geometry.Bounds);
                }

                if (visual != null)
                {
                    Deindex(scene, visual);
                }
            }

            if (node.LayerRoot == node.Visual)
            {
                scene.Layers.Remove(node.LayerRoot);
            }
        }

        private static void ClearLayer(Scene scene, VisualNode node)
        {
            var parent = (VisualNode)node.Parent;
            var oldLayerRoot = node.LayerRoot;
            var newLayerRoot = parent.LayerRoot;
            var existingDirtyRects = scene.Layers[node.LayerRoot].Dirty;
            var newDirtyRects = scene.Layers[newLayerRoot].Dirty;

            existingDirtyRects.Coalesce();

            foreach (var r in existingDirtyRects)
            {
                newDirtyRects.Add(r);
            }

            var oldLayer = scene.Layers[oldLayerRoot];
            SetDescendentsLayer(node, scene.Layers[newLayerRoot], oldLayer);
            scene.Layers.Remove(oldLayer);
        }

        private static void MakeLayer(Scene scene, VisualNode node)
        {
            var oldLayerRoot = node.LayerRoot;
            var layer = scene.Layers.Add(node.Visual);
            var oldLayer = scene.Layers[oldLayerRoot];

            UpdateLayer(node, layer);
            SetDescendentsLayer(node, layer, scene.Layers[oldLayerRoot]);
        }

        private static void UpdateLayer(VisualNode node, SceneLayer layer)
        {
            layer.Opacity = node.Visual.Opacity;

            if (node.Visual.OpacityMask != null)
            {
                layer.OpacityMask = ToImmutable(node.Visual.OpacityMask);
                layer.OpacityMaskRect = node.ClipBounds;
            }
            else
            {
                layer.OpacityMask = null;
                layer.OpacityMaskRect = Rect.Empty;
            }
        }

        private static void SetDescendentsLayer(VisualNode node, SceneLayer layer, SceneLayer oldLayer)
        {
            node.LayerRoot = layer.LayerRoot;

            layer.Dirty.Add(node.Bounds);
            oldLayer.Dirty.Add(node.Bounds);

            foreach (VisualNode child in node.Children)
            {
                // If the child is not the start of a new layer, recurse.
                if (child.LayerRoot != child.Visual)
                {
                    SetDescendentsLayer(child, layer, oldLayer);
                }
            }
        }

        private static IBrush ToImmutable(IBrush brush)
        {
            return (brush as IMutableBrush)?.ToImmutable() ?? brush;
        }
    }
}
