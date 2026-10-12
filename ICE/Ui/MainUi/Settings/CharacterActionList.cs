using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ICE.ConfigFiles;
using System.Collections.Generic;
using static ICE.Localization.L10n;

namespace ICE.Ui.MainUi.Settings
{
    /// <summary>
    /// 角色动作列表（改编自 AutoDuty ConfigurationProfileV2 的循环动作列表）：
    /// 拖拽排序、按住 Ctrl 删除、「+」添加、勾选启用、点击展开设置。
    /// </summary>
    internal static class CharacterActionList
    {
        private static readonly Vector4 LinkColor = new(0.25f, 0.8f, 1f, 1f);
        private static readonly Vector4 AccentRed = new(1f, 0.35f, 0.35f, 1f);

        // 每个列表各自的展开状态 / 拖拽来源 / 列表高度，按 id 区分
        private static readonly Dictionary<CharacterAction, bool> ConfigOpen = new(ReferenceEqualityComparer.Instance);
        private static readonly Dictionary<string, int> DragSource = new();
        private static readonly Dictionary<string, float> ListHeight = new();

        public static void Draw(string id, List<CharacterAction> actions, bool editable = true)
        {
            float height = ListHeight.GetValueOrDefault(id, 200f);
            if (!ImGui.BeginListBox($"##CharacterActions_{id}", new Vector2(ImGui.GetContentRegionAvail().X, height)))
                return;

            // ImGui payload 类型名最长 32 个字符
            string dragDropId = $"ICEAction{(uint)id.GetHashCode():X8}";
            int dragSource = DragSource.GetValueOrDefault(id, -1);
            int moveFrom = -1;
            int moveTo = -1;
            int dragging = -1;

            using (ImRaii.Disabled(!editable))
            {
                for (int index = 0; index < actions.Count; index++)
                {
                    using var _ = ImRaii.PushId($"{id}_{index}");
                    var action = actions[index];

                    ImGui.BeginGroup();

                    using (ImRaii.PushFont(UiBuilder.IconFont))
                        ImGui.Button($"{FontAwesomeIcon.GripVertical.ToIconString()}###drag", new Vector2(28f * ImGuiHelpers.GlobalScale, 0));
                    if (ImGui.IsItemHovered())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);

                    using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.75f))
                    {
                        if (editable && ImGui.BeginDragDropSource())
                        {
                            ImGuiDragDrop.SetDragDropPayload(dragDropId, index.ToString());
                            ImGui.TextUnformatted(action.Name);
                            ImGui.EndDragDropSource();
                            dragging = index;
                        }
                    }

                    ImGui.SameLine();

                    bool deleted = false;
                    if (editable && ImGui.GetIO().KeyCtrl)
                    {
                        using (ImRaii.PushColor(ImGuiCol.Button, AccentRed with { W = 0.15f }))
                        using (ImRaii.PushColor(ImGuiCol.Text, AccentRed))
                        using (ImRaii.PushFont(UiBuilder.IconFont))
                        {
                            if (ImGui.Button($"{FontAwesomeIcon.TrashAlt.ToIconString()}###delete", new Vector2(28f * ImGuiHelpers.GlobalScale, 0)))
                                deleted = true;
                        }
                        ImGui.SameLine();
                    }

                    if (deleted)
                    {
                        ImGui.EndGroup();
                        actions.RemoveAt(index);
                        ConfigOpen.Remove(action);
                        index--;
                        C.Save();
                        continue;
                    }

                    DrawEntry(action);

                    ImGui.EndGroup();

                    Vector2 rowMin = ImGui.GetItemRectMin();
                    Vector2 rowMax = ImGui.GetItemRectMax();
                    var drawList = ImGui.GetWindowDrawList();

                    // 拖动中的条目变暗
                    if (index == dragging || index == dragSource)
                        drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(new Vector4(0, 0, 0, 0.5f)));

                    if (editable && ImGui.BeginDragDropTarget())
                    {
                        var payload = ImGui.AcceptDragDropPayload(dragDropId, ImGuiDragDropFlags.AcceptBeforeDelivery | ImGuiDragDropFlags.AcceptNoDrawDefaultRect);
                        if (!payload.IsNull && dragSource >= 0 && dragSource != index)
                        {
                            // 插入位置提示线：向下拖画在目标下方，向上拖画在目标上方
                            float y = dragSource < index ? rowMax.Y : rowMin.Y;
                            drawList.AddLine(new Vector2(rowMin.X, y), new Vector2(rowMax.X, y), ImGui.GetColorU32(ImGuiCol.DragDropTarget), 2f);

                            if (payload.IsDelivery())
                            {
                                moveFrom = dragSource;
                                moveTo = index;
                            }
                        }
                        ImGui.EndDragDropTarget();
                    }
                }
            }

            DragSource[id] = dragging;

            if (moveFrom >= 0 && moveFrom < actions.Count && moveFrom != moveTo)
            {
                var moved = actions[moveFrom];
                actions.RemoveAt(moveFrom);
                actions.Insert(moveTo, moved);
                C.Save();
            }

            ListHeight[id] = Math.Max(ImGui.GetCursorPosY(), 30f);
            ImGui.EndListBox();

            if (!editable)
                return;

            using (ImRaii.PushFont(UiBuilder.IconFont))
            {
                if (ImGui.Button($"{FontAwesomeIcon.Plus.ToIconString()}###addAction{id}", new Vector2(28f * ImGuiHelpers.GlobalScale, 0)))
                    ImGui.OpenPopup($"##CharacterActions{id}Add");
            }
            ImGui.SameLine();
            ImGui.TextDisabled(T("Hold CTRL to delete entries, drag to reorder"));

            if (ImGui.BeginPopup($"##CharacterActions{id}Add", ImGuiWindowFlags.AlwaysAutoResize))
            {
                bool any = false;
                foreach (var (type, create) in CharacterAction.Available)
                {
                    // 同类动作只会执行第一个，不重复添加
                    if (actions.Any(a => a.GetType() == type))
                        continue;

                    any = true;
                    var sample = create();
                    if (ImGui.Selectable($"{sample.Name}##{id}_{type.Name}"))
                    {
                        actions.Add(sample);
                        ConfigOpen[sample] = sample.HasConfig;
                        C.Save();
                    }
                }
                if (!any)
                    ImGui.TextDisabled(T("All actions are already in the list"));
                ImGui.EndPopup();
            }
        }

        private static void DrawEntry(CharacterAction action)
        {
            bool open = ConfigOpen.GetValueOrDefault(action, false) && action.HasConfig;
            bool pluginMissing = action.RequiredPlugin is { } required && !Utils.HasPlugin(required.InternalName);

            bool enabled = action.Enabled;
            if (ImGui.Checkbox("##Enabled", ref enabled))
            {
                action.Enabled = enabled;
                C.Save();
            }
            ImGui.SameLine();

            if (action.HasConfig)
            {
                ImGui.SetItemAllowOverlap();
                if (ImGui.Selectable("##Expand", ref open))
                    ConfigOpen[action] = open;
                ImGui.SameLine(0, 0);
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextUnformatted((open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight).ToIconString());
                ImGui.SameLine(0, 0);
            }

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(action.Name);

            if (action.RequiredPlugin is { } plugin)
            {
                ImGui.SameLine();
                if (pluginMissing)
                {
                    ImGui.TextColored(EColor.RedBright, T("| requires {0}", plugin.Display));
                }
                else
                {
                    ImGui.TextDisabled("| powered by");
                    ImGui.SameLine(0, 4);
                    ImGui.TextColored(LinkColor, plugin.Display);
                }
            }

            if (action.HelpText != null)
                ImGuiEx.HelpMarker(action.HelpText);

            if (open)
            {
                ImGui.Separator();
                ImGui.Indent();
                action.DrawSettings();
                ImGui.Unindent();
                ImGui.Separator();
            }
        }
    }
}
