using System;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Orcub.EditorTools
{
    /// <summary>
    /// Dev-only: keeps the MCP for Unity editor bridge connected. The package auto-starts its session only
    /// once per editor launch, and a brief server health blip ends that session for good ("ending orphaned
    /// session"), leaving the automation agents with no Unity. Every 15 s, if the local MCP server is reachable
    /// and the bridge is not running, this reconnects it — the same call the window's Start Session button makes.
    /// Uses reflection so the project still compiles if the package is removed.
    /// </summary>
    [InitializeOnLoad]
    static class McpWatchdog
    {
        const double Interval = 15.0;
        static double _next;
        static bool _busy;
        static object _bridge, _server;
        static PropertyInfo _isRunning;
        static MethodInfo _start, _reachable;
        static bool _resolved, _available;

        static McpWatchdog()
        {
            if (Application.isBatchMode) return;
            _next = EditorApplication.timeSinceStartup + 20.0;   // let the package's own auto-start go first
            EditorApplication.update += Tick;
        }

        static bool Resolve()
        {
            if (_resolved) return _available;
            _resolved = true;
            try
            {
                var locator = Type.GetType("MCPForUnity.Editor.Services.MCPServiceLocator, MCPForUnity.Editor");
                if (locator == null) return false;
                _bridge = locator.GetProperty("Bridge", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                _server = locator.GetProperty("Server", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (_bridge == null || _server == null) return false;
                var bridgeType = Type.GetType("MCPForUnity.Editor.Services.IBridgeControlService, MCPForUnity.Editor");
                var serverType = Type.GetType("MCPForUnity.Editor.Services.IServerManagementService, MCPForUnity.Editor");
                _isRunning = bridgeType?.GetProperty("IsRunning");
                _start = bridgeType?.GetMethod("StartAsync", Type.EmptyTypes);
                _reachable = serverType?.GetMethod("IsLocalHttpServerReachable", Type.EmptyTypes);
                _available = _isRunning != null && _start != null && _reachable != null;
            }
            catch (Exception) { _available = false; }
            return _available;
        }

        static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _next || _busy) return;
            _next = now + Interval;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (!Resolve()) { EditorApplication.update -= Tick; return; }
            try
            {
                if ((bool)_isRunning.GetValue(_bridge)) return;
                if (!(bool)_reachable.Invoke(_server, null)) return;
                Reconnect();
            }
            catch (Exception e) { Debug.LogWarning("[Orcub] MCP watchdog: " + (e.InnerException ?? e).Message); }
        }

        static async void Reconnect()
        {
            _busy = true;
            try
            {
                Debug.Log("[Orcub] MCP bridge down while server is up; reconnecting");
                var ok = await (Task<bool>)_start.Invoke(_bridge, null);
                Debug.Log(ok ? "[Orcub] MCP bridge reconnected" : "[Orcub] MCP bridge reconnect failed; will retry");
            }
            catch (Exception e) { Debug.LogWarning("[Orcub] MCP watchdog reconnect: " + (e.InnerException ?? e).Message); }
            finally { _busy = false; }
        }
    }
}
