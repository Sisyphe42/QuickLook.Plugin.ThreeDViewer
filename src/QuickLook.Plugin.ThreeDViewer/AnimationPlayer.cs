using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Assimp;
using WpfVector3D = System.Windows.Media.Media3D.Vector3D;
using WpfQuaternion = System.Windows.Media.Media3D.Quaternion;
using AiQuaternion = Assimp.Quaternion;
using AiVector3D = Assimp.Vector3D;

namespace QuickLook.Plugin.ThreeDViewer
{
    /// <summary>
    /// Drives Assimp node animations on the WPF visual tree.
    ///
    /// Verified contract (AssimpNet 5.0.0-beta1):
    ///   Animation.DurationInTicks / TicksPerSecond / NodeAnimationChannels
    ///   NodeAnimationChannel.NodeName / PositionKeys / RotationKeys / ScalingKeys
    ///   VectorKey.Time(double) + Value(Vector3D); QuaternionKey.Value(Quaternion W/X/Y/Z)
    /// Uses CompositionTarget.Rendering (once per frame) rather than a DispatcherTimer
    /// so playback stays smooth and stops automatically when the control unloads.
    /// </summary>
    internal sealed class AnimationPlayer : IDisposable
    {
        private readonly BuiltScene _built;
        private readonly List<Animation> _animations;
        private readonly Dispatcher _dispatcher;
        private readonly List<ChannelBinding> _channels = new List<ChannelBinding>();
        private bool _attached;
        private bool _disposed;
        private double _elapsedSeconds;
        private long _lastTick;
        private double _durationSeconds;

        internal bool IsPlaying { get { return _attached && !_disposed; } }
        internal int AnimationCount { get { return _animations.Count; } }
        internal string Label { get; private set; }

        private sealed class ChannelBinding
        {
            internal ModelVisual3D Visual;
            internal Matrix3D Rest;
            internal NodeAnimationChannel Channel;
        }

        internal AnimationPlayer(BuiltScene built, Scene scene, FrameworkElement owner)
        {
            _built = built;
            _dispatcher = owner.Dispatcher;
            _animations = new List<Animation>();
            if (scene.HasAnimations)
            {
                foreach (Animation a in scene.Animations)
                {
                    if (a.HasNodeAnimations) _animations.Add(a);
                }
            }
            Label = _animations.Count > 0
                ? string.Format("{0} 个动画 · 自动播放", _animations.Count)
                : "无动画";

            if (_animations.Count == 0) return;

            // Use the first animation as the timeline. glTF usually has one clip;
            // overlapping clips would need a mixer, which is out of scope for a
            // lightweight preview.
            Animation clip = _animations[0];
            _durationSeconds = clip.TicksPerSecond > 0
                ? clip.DurationInTicks / clip.TicksPerSecond
                : clip.DurationInTicks / 25.0;
            if (_durationSeconds <= 0) _durationSeconds = 1.0;

            foreach (NodeAnimationChannel ch in clip.NodeAnimationChannels)
            {
                ModelVisual3D visual;
                Matrix3D rest;
                if (string.IsNullOrEmpty(ch.NodeName) ||
                    !_built.Nodes.TryGetValue(ch.NodeName, out visual) ||
                    !_built.RestTransforms.TryGetValue(ch.NodeName, out rest))
                    continue;
                _channels.Add(new ChannelBinding { Visual = visual, Rest = rest, Channel = ch });
            }

            _lastTick = Environment.TickCount;
            CompositionTarget.Rendering += OnRendering;
            _attached = true;
            Diagnostics.Log("Animation started: " + _channels.Count + " channels, duration " + _durationSeconds + "s");
        }

        private void OnRendering(object sender, EventArgs e)
        {
            if (_disposed) return;
            long now = Environment.TickCount;
            double dt = (now - _lastTick) / 1000.0;
            _lastTick = now;
            if (dt < 0 || dt > 0.25) dt = 1.0 / 60.0; // clamp after stalls / first frame
            _elapsedSeconds += dt;
            double timeTicks = (_elapsedSeconds % _durationSeconds) * (_animations[0].TicksPerSecond > 0
                ? _animations[0].TicksPerSecond : 25.0);

            foreach (ChannelBinding b in _channels)
            {
                var m = b.Rest;
                WpfVector3D pos = new WpfVector3D(m.OffsetX, m.OffsetY, m.OffsetZ);
                WpfVector3D scale = ExtractScale(ref m);
                Matrix3D rot = ExtractRotation(ref m);

                AiVector3D p;
                if (TryVector(b.Channel.PositionKeys, timeTicks, out p)) pos = new WpfVector3D(p.X, p.Y, p.Z);
                AiVector3D s;
                if (TryVector(b.Channel.ScalingKeys, timeTicks, out s)) scale = new WpfVector3D(s.X, s.Y, s.Z);

                AiQuaternion q;
                if (TryQuaternion(b.Channel.RotationKeys, timeTicks, out q))
                {
                    var rq = new WpfQuaternion(q.X, q.Y, q.Z, q.W);
                    rq.Normalize();
                    var rm = Matrix3D.Identity;
                    rm.Rotate(rq);
                    rot = rm;
                }

                // Compose: Scale * Rotation * Translation (row-vector convention).
                var compose = new Matrix3D(
                    rot.M11 * scale.X, rot.M12 * scale.X, rot.M13 * scale.X, 0,
                    rot.M21 * scale.Y, rot.M22 * scale.Y, rot.M23 * scale.Y, 0,
                    rot.M31 * scale.Z, rot.M32 * scale.Z, rot.M33 * scale.Z, 0,
                    pos.X, pos.Y, pos.Z, 1);
                b.Visual.Transform = new MatrixTransform3D(compose);
            }
        }

        private static WpfVector3D ExtractScale(ref Matrix3D m)
        {
            double sx = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13);
            double sy = Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23);
            double sz = Math.Sqrt(m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33);
            if (sx < 1e-6) sx = 1; if (sy < 1e-6) sy = 1; if (sz < 1e-6) sz = 1;
            return new WpfVector3D(sx, sy, sz);
        }

        private static Matrix3D ExtractRotation(ref Matrix3D m)
        {
            double sx = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13);
            double sy = Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23);
            double sz = Math.Sqrt(m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33);
            if (sx < 1e-6) sx = 1; if (sy < 1e-6) sy = 1; if (sz < 1e-6) sz = 1;
            var r = new Matrix3D(
                m.M11 / sx, m.M12 / sx, m.M13 / sx, 0,
                m.M21 / sy, m.M22 / sy, m.M23 / sy, 0,
                m.M31 / sz, m.M32 / sz, m.M33 / sz, 0,
                0, 0, 0, 1);
            if (r.HasInverse) return r;
            return Matrix3D.Identity;
        }

        private static bool TryVector(List<VectorKey> keys, double time, out AiVector3D value)
        {
            value = new AiVector3D();
            if (keys == null || keys.Count == 0) return false;
            if (time <= keys[0].Time) { value = ToVector(keys[0].Value); return true; }
            if (time >= keys[keys.Count - 1].Time) { value = ToVector(keys[keys.Count - 1].Value); return true; }
            int lo = 0, hi = keys.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].Time <= time) lo = mid; else hi = mid;
            }
            VectorKey a = keys[lo], b = keys[hi];
            double span = b.Time - a.Time;
            float t = span > 0 ? (float)((time - a.Time) / span) : 0f;
            AiVector3D va = ToVector(a.Value), vb = ToVector(b.Value);
            value = va + (vb - va) * t;
            return true;
        }

        private static bool TryQuaternion(List<QuaternionKey> keys, double time, out AiQuaternion value)
        {
            value = new AiQuaternion();
            if (keys == null || keys.Count == 0) return false;
            if (time <= keys[0].Time) { value = keys[0].Value; return true; }
            if (time >= keys[keys.Count - 1].Time) { value = keys[keys.Count - 1].Value; return true; }
            int lo = 0, hi = keys.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].Time <= time) lo = mid; else hi = mid;
            }
            QuaternionKey a = keys[lo], b = keys[hi];
            double span = b.Time - a.Time;
            float t = span > 0 ? (float)((time - a.Time) / span) : 0f;
            value = AiQuaternion.Slerp(a.Value, b.Value, t);
            return true;
        }

        private static AiVector3D ToVector(AiVector3D v) { return new AiVector3D(v.X, v.Y, v.Z); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_attached)
            {
                try { CompositionTarget.Rendering -= OnRendering; }
                catch { }
                _attached = false;
            }
            _channels.Clear();
        }
    }
}
