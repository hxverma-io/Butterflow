using System;
using System.Drawing;
using System.Windows.Forms;
using ButterFlow.Core;

namespace ButterFlow.Engine
{
    public sealed class EdgeTransitionEngine
    {
        private ScreenEdge _configuredEdge = ScreenEdge.Right;
        private int _edgeBarrierThreshold = 1; // 1 = snappy instant, 2-3 = resistance
        private int _edgePushCount = 0;
        private bool _isControllingRemote = false;

        public event Action<float> OnTransitionToRemote; // Normalized entry coordinate (0.0 to 1.0)
        public event Action<float> OnReturnToHost;

        public ScreenEdge ConfiguredEdge
        {
            get { return _configuredEdge; }
            set { _configuredEdge = value; }
        }

        public int EdgeResistance
        {
            get { return _edgeBarrierThreshold; }
            set { _edgeBarrierThreshold = Math.Max(1, Math.Min(10, value)); }
        }

        public bool IsControllingRemote
        {
            get { return _isControllingRemote; }
            set { _isControllingRemote = value; }
        }

        public bool CheckEdgeCrossing(int cursorX, int cursorY, int screenWidth, int screenHeight)
        {
            if (_isControllingRemote) return false;

            bool hit = false;
            float normPos = 0.5f;

            switch (_configuredEdge)
            {
                case ScreenEdge.Right:
                    if (cursorX >= screenWidth - 2)
                    {
                        _edgePushCount++;
                        normPos = (float)cursorY / Math.Max(1, screenHeight);
                        hit = _edgePushCount >= _edgeBarrierThreshold;
                    }
                    else
                    {
                        _edgePushCount = 0;
                    }
                    break;

                case ScreenEdge.Left:
                    if (cursorX <= 1)
                    {
                        _edgePushCount++;
                        normPos = (float)cursorY / Math.Max(1, screenHeight);
                        hit = _edgePushCount >= _edgeBarrierThreshold;
                    }
                    else
                    {
                        _edgePushCount = 0;
                    }
                    break;

                case ScreenEdge.Top:
                    if (cursorY <= 1)
                    {
                        _edgePushCount++;
                        normPos = (float)cursorX / Math.Max(1, screenWidth);
                        hit = _edgePushCount >= _edgeBarrierThreshold;
                    }
                    else
                    {
                        _edgePushCount = 0;
                    }
                    break;

                case ScreenEdge.Bottom:
                    if (cursorY >= screenHeight - 2)
                    {
                        _edgePushCount++;
                        normPos = (float)cursorX / Math.Max(1, screenWidth);
                        hit = _edgePushCount >= _edgeBarrierThreshold;
                    }
                    else
                    {
                        _edgePushCount = 0;
                    }
                    break;
            }

            if (hit)
            {
                _edgePushCount = 0;
                _isControllingRemote = true;
                if (OnTransitionToRemote != null)
                {
                    OnTransitionToRemote(Math.Max(0f, Math.Min(1f, normPos)));
                }
                return true;
            }

            return false;
        }

        public void ProcessReturnEdge(float returnNormPos, int screenWidth, int screenHeight)
        {
            _isControllingRemote = false;
            _edgePushCount = 0;

            int targetX = screenWidth / 2;
            int targetY = screenHeight / 2;

            switch (_configuredEdge)
            {
                case ScreenEdge.Right:
                    targetX = screenWidth - 10;
                    targetY = (int)(returnNormPos * screenHeight);
                    break;
                case ScreenEdge.Left:
                    targetX = 10;
                    targetY = (int)(returnNormPos * screenHeight);
                    break;
                case ScreenEdge.Top:
                    targetX = (int)(returnNormPos * screenWidth);
                    targetY = 10;
                    break;
                case ScreenEdge.Bottom:
                    targetX = (int)(returnNormPos * screenWidth);
                    targetY = screenHeight - 10;
                    break;
            }

            NativeMethods.SetCursorPos(targetX, targetY);

            if (OnReturnToHost != null)
            {
                OnReturnToHost(returnNormPos);
            }
        }
    }
}
