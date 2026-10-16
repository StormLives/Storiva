using Storiva.App.Icons;
using Storiva.App.Theme;
using Storiva.Core.Commands;

namespace Storiva.App.Commands;

/// <summary>
/// Builds the menu bar (and context menus) from <see cref="CommandCatalog"/>. Enabled/checked states are
/// re-evaluated every time a drop-down opens, so the main form does not track menu items by hand.
/// Shortcut texts are display-only: keys are routed centrally by <see cref="ShortcutRouter"/>.
/// </summary>
internal sealed class MenuBuilder
{
    private readonly Func<CommandId, bool> _isEnabled;
    private readonly Func<CommandId, bool?> _isChecked;
    private readonly Action<CommandId> _execute;
    private readonly Func<string, IReadOnlyList<ToolStripItem>> _dynamicItems;
    private static StorivaPalette _palette => StorivaPalette.Current;
    private readonly Dictionary<CommandId, List<ToolStripMenuItem>> _items = new();
    private int _iconSize = 16;

    public MenuBuilder(
        Func<CommandId, bool> isEnabled,
        Func<CommandId, bool?> isChecked,
        Action<CommandId> execute,
        Func<string, IReadOnlyList<ToolStripItem>> dynamicItems)
    {
        _isEnabled = isEnabled;
        _isChecked = isChecked;
        _execute = execute;
        _dynamicItems = dynamicItems;
    }

    /// <summary>Every menu item created for a command (menu bar and context menus).</summary>
    public IReadOnlyDictionary<CommandId, List<ToolStripMenuItem>> Items => _items;

    public void BuildMenuBar(MenuStrip menu)
    {
        foreach (var definition in CommandCatalog.Menus)
        {
            var top = new ToolStripMenuItem(definition.Title);
            Populate(top.DropDownItems, definition.Entries);
            top.DropDownOpening += (_, _) => Refresh(top.DropDownItems);
            menu.Items.Add(top);
        }
    }

    /// <summary>Context menu with the given commands (null = separator).</summary>
    public ContextMenuStrip BuildContextMenu(IEnumerable<CommandId?> commands, ToolStripRenderer renderer)
    {
        var strip = new ContextMenuStrip { Renderer = renderer };
        foreach (var id in commands)
        {
            strip.Items.Add(id is { } command ? CreateItem(command) : new ToolStripSeparator());
        }

        strip.Opening += (_, _) => Refresh(strip.Items);
        return strip;
    }

    /// <summary>Re-renders icons at a new size (DPI change).</summary>
    public void ApplyIconSize(int pixels)
    {
        _iconSize = pixels;
        foreach (var (id, items) in _items)
        {
            var icon = CommandCatalog.Get(id).Icon;
            foreach (var item in items)
            {
                item.Image = icon is null ? null : LucideIcons.Get(icon, pixels, _palette.IconDefault);
            }
        }
    }

    /// <summary>Updates enabled/checked state of the items below <paramref name="items"/> (recursively).</summary>
    public void Refresh(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            if (item.Tag is CommandId id && item is ToolStripMenuItem menuItem)
            {
                menuItem.Enabled = _isEnabled(id);
                if (_isChecked(id) is { } isChecked)
                {
                    menuItem.Checked = isChecked;
                }
            }
        }

        RefreshDynamicSections(items);
    }

    private void Populate(ToolStripItemCollection target, IReadOnlyList<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case CommandEntry command:
                    target.Add(CreateItem(command.Id));
                    break;
                case SeparatorEntry:
                    target.Add(new ToolStripSeparator());
                    break;
                case SubmenuEntry submenu:
                {
                    var definition = CommandCatalog.Get(submenu.Id);
                    var parent = new ToolStripMenuItem(submenu.Title ?? definition.Text) { Tag = submenu.Id };
                    if (submenu.DynamicList is { } list)
                    {
                        parent.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
                        parent.DropDownOpening += (_, _) =>
                        {
                            parent.DropDownItems.Clear();
                            parent.DropDownItems.AddRange(_dynamicItems(list).ToArray());
                        };
                    }
                    else
                    {
                        Populate(parent.DropDownItems, submenu.Children);
                        parent.DropDownOpening += (_, _) => Refresh(parent.DropDownItems);
                    }

                    // Storiva's "Layout" submenu is always enabled; others follow their command.
                    if (submenu.Title is not null)
                    {
                        parent.Tag = null;
                    }
                    else
                    {
                        Register(submenu.Id, parent);
                    }

                    target.Add(parent);
                    break;
                }
                case DynamicEntry dynamic:
                    // Placeholder separator marks where dynamic items go; filled on Refresh.
                    target.Add(new ToolStripSeparator { Tag = new DynamicMarker(dynamic.ListName), Visible = false });
                    break;
            }
        }
    }

    private void RefreshDynamicSections(ToolStripItemCollection items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Tag is not DynamicMarker marker)
            {
                continue;
            }

            // Remove previously generated items that follow the marker.
            while (i + 1 < items.Count && items[i + 1].Tag is GeneratedTag)
            {
                items.RemoveAt(i + 1);
            }

            var generated = _dynamicItems(marker.ListName);
            for (var j = 0; j < generated.Count; j++)
            {
                generated[j].Tag ??= new GeneratedTag();
                if (generated[j].Tag is not GeneratedTag)
                {
                    // Keep the caller's tag but wrap it so the item can be recognised later.
                    generated[j].Tag = new GeneratedTag(generated[j].Tag);
                }

                items.Insert(i + 1 + j, generated[j]);
            }

            i += generated.Count;
        }
    }

    private ToolStripMenuItem CreateItem(CommandId id)
    {
        var definition = CommandCatalog.Get(id);
        var item = new ToolStripMenuItem(definition.Text)
        {
            Tag = id,
            ShortcutKeyDisplayString = definition.Shortcut,
            ShowShortcutKeys = definition.Shortcut is not null,
            ToolTipText = definition.Behavior == CommandBehavior.Unavailable ? "Not available in Storiva yet" : null,
            Image = definition.Icon is null ? null : LucideIcons.Get(definition.Icon, _iconSize, _palette.IconDefault),
        };
        if (definition.Behavior == CommandBehavior.Unavailable)
        {
            item.ForeColor = _palette.TextSecondary;
        }

        item.Click += (_, _) => _execute(id);
        Register(id, item);
        return item;
    }

    private void Register(CommandId id, ToolStripMenuItem item)
    {
        if (!_items.TryGetValue(id, out var list))
        {
            _items[id] = list = [];
        }

        list.Add(item);
    }

    private sealed record DynamicMarker(string ListName);

    /// <summary>Marks items produced by a dynamic list; <see cref="Payload"/> keeps the producer's own tag.</summary>
    internal sealed record GeneratedTag(object? Payload = null);
}
