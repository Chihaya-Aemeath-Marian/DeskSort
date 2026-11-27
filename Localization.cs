using System;
using System.Collections.Generic;
using System.Windows;

namespace DeskSort
{
    internal static class Localization
    {
        private static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]>
        {
            ["AppTitle"] = new[] { "デスク整理", "桌面整理", "Desktop Organizer" },
            ["ShowDesktopMenu"] = new[] { "デスクトップバーを表示", "显示桌面主菜单", "Show desktop menu" },
            ["HideDesktopMenu"] = new[] { "デスクトップバーを隠す", "隐藏桌面主菜单", "Hide desktop menu" },
            ["DisplayMonitor"] = new[] { "表示するモニター", "显示器选择", "Display monitor" },
            ["OnlyOneDisplay"] = new[] { "使用できるモニターは1台のみです", "当前只有一个可用显示器", "Only one display is available" },
            ["PrimaryDisplay"] = new[] { "メイン", "主显示器", "Primary" },
            ["Categories"] = new[] { "カテゴリー表示", "分类显示", "Categories" },
            ["Subtitle"] = new[] { "カテゴリーは個別のデスクトップ領域 · 他のウィンドウを通常どおり前面に表示できます", "分类是独立的桌面区域 · 其他窗口可以正常置顶使用", "Categories are separate desktop areas · Other windows can stay in front" },
            ["SearchDesktop"] = new[] { "デスクトップ項目を検索", "搜索桌面项目", "Search desktop items" },
            ["AddCategory"] = new[] { "＋ 追加", "＋ 添加", "＋ Add" },
            ["AutoStart"] = new[] { "ログイン時に起動", "登录时启动", "Run at login" },
            ["Refresh"] = new[] { "デスクトップを更新", "刷新桌面", "Refresh desktop" },
            ["ShowAll"] = new[] { "すべて表示", "全部显示", "Show all" },
            ["ExitRestore"] = new[] { "終了してデスクトップアイコンを復元", "退出并恢复桌面图标", "Exit and restore desktop icons" },
            ["Language"] = new[] { "表示言語", "显示语言", "Display language" },
            ["Profiles"] = new[] { "設定", "配置", "Profiles" },
            ["CreateProfile"] = new[] { "新しい設定を保存…", "另存为新配置…", "Save as new profile…" },
            ["ProfileName"] = new[] { "設定名", "配置名称", "Profile name" },
            ["DefaultProfile"] = new[] { "デフォルト", "默认", "Default" },
            ["ProfileExists"] = new[] { "同じ名前の設定がすでにあります。", "已存在同名配置。", "A profile with that name already exists." },
            ["ProfileError"] = new[] { "設定を作成できませんでした: ", "无法创建配置：", "Could not create profile: " },
            ["FileIconTooltip"] = new[] { "右クリックでこの項目のアイコンをカスタマイズ", "右键自定义此项目的图标", "Right-click to customize this item's icon" },
            ["OpenFile"] = new[] { "開く", "打开", "Open" },
            ["CategoryImageTooltip"] = new[] { "クリックしてカテゴリー画像を変更", "点击更改分类图标", "Click to change category image" },
            ["EditName"] = new[] { "クリックして名前を編集", "点击编辑名称", "Click to edit name" },
            ["ToggleCategory"] = new[] { "カテゴリーを展開・折りたたむ", "展开或收起分类", "Expand or collapse category" },
            ["MoveCategory"] = new[] { "ドラッグして位置を変更", "拖动调整位置", "Drag to move" },
            ["CategorySettings"] = new[] { "カテゴリー設定", "分类设置", "Category settings" },
            ["EmptyHint"] = new[] { "デスクトップの項目をここにドラッグして分類", "将桌面项目拖到这里进行分类", "Drag desktop items here to organize them" },
            ["ResizeCategory"] = new[] { "ドラッグしてウィンドウを拡大・縮小", "拖动调整分类区域大小", "Drag to resize category" },
            ["HideCategory"] = new[] { "カテゴリーを非表示", "隐藏分类", "Hide category" },
            ["ShowCategory"] = new[] { "カテゴリーを表示", "显示分类", "Show category" },
            ["SelectImage"] = new[] { "画像を選択…", "选择图标图片…", "Choose icon image…" },
            ["SelectBackground"] = new[] { "背景画像を選択…", "选择背景图片…", "Choose background image…" },
            ["SelectSlideshowFolder"] = new[] { "画像フォルダーをスライドショー表示…", "选择图片文件夹并播放幻灯片…", "Choose slideshow image folder…" },
            ["ClearSlideshow"] = new[] { "スライドショーを解除", "关闭幻灯片播放", "Stop slideshow" },
            ["AdjustBackground"] = new[] { "背景画像のサイズを調整…", "调整背景图片显示…", "Adjust background image…" },
            ["ResetBackground"] = new[] { "背景画像をリセット", "重置背景图片", "Reset background image" },
            ["DeleteCategory"] = new[] { "カテゴリーを削除（項目は別の分類へ移動）", "删除分类（项目将移至其他分类）", "Delete category (move items to another category)" },
            ["SlideshowDescription"] = new[] { "スライドショーに使用する画像フォルダーを選択してください。", "请选择用于幻灯片播放的图片文件夹。", "Choose a folder for the slideshow images." },
            ["NoImages"] = new[] { "選択したフォルダーに対応する画像がありません。", "所选文件夹中没有支持的图片。", "No supported images were found in that folder." },
            ["BackgroundDialogTitle"] = new[] { "背景画像の表示を調整", "调整背景图片显示", "Adjust background image" },
            ["DisplayMode"] = new[] { "表示方法", "显示方式", "Display mode" },
            ["FitWhole"] = new[] { "全体を表示", "完整显示", "Fit entire image" },
            ["CropToFrame"] = new[] { "枠に合わせてトリミング", "填满区域并裁切", "Fill and crop" },
            ["StretchToFrame"] = new[] { "枠に合わせて変形", "拉伸至填满区域", "Stretch to fill" },
            ["Scale"] = new[] { "拡大・縮小", "缩放", "Scale" },
            ["PreviewHint"] = new[] { "プレビュー内をドラッグして位置調整、ホイールで拡大・縮小", "在预览区拖动调整位置，滚轮缩放", "Drag in preview to position; scroll to zoom" },
            ["Cancel"] = new[] { "キャンセル", "取消", "Cancel" },
            ["Apply"] = new[] { "適用", "应用", "Apply" },
            ["DeleteConfirmTitle"] = new[] { "カテゴリーを削除", "删除分类", "Delete category" },
            ["DeleteConfirm"] = new[] { "「{0}」を削除しますか？ {1} 件の項目は「{2}」へ移動します。ファイル自体は移動しません。", "确定删除“{0}”吗？其中 {1} 个项目将移至“{2}”，文件本身不会移动。", "Delete “{0}”? Its {1} items will be reassigned to “{2}”; the files themselves will not move." },
            ["FileOpenError"] = new[] { "ファイルを開けません", "无法打开文件", "Unable to open file" },
            ["ImageLoadError"] = new[] { "画像を読み込めませんでした: ", "无法读取图片：", "Could not load image: " },
            ["Folder"] = new[] { "フォルダー", "文件夹", "Folder" },
            ["Docs"] = new[] { "書類", "文档", "Documents" },
            ["Images"] = new[] { "画像", "图片", "Images" },
            ["Sheets"] = new[] { "表計算", "表格", "Spreadsheets" },
            ["Installers"] = new[] { "アプリ・インストーラー", "应用和安装包", "Apps & Installers" },
            ["Other"] = new[] { "その他", "其他", "Other" },
            ["AudioStart"] = new[] { "システム音声に反応する波形を開始", "启动系统声音波形", "Start system audio visualizer" },
            ["AudioStop"] = new[] { "システム音声に反応する波形を停止", "停止系统声音波形", "Stop system audio visualizer" },
            ["AudioTitle"] = new[] { "システム音声の波形", "系统声音波形", "System Audio Visualizer" },
            ["AudioMove"] = new[] { "ドラッグして波形モジュールを移動", "拖动移动波形模块", "Drag to move visualizer" },
            ["AudioClose"] = new[] { "波形を閉じる", "关闭波形", "Close visualizer" },
            ["AudioResize"] = new[] { "ドラッグしてモジュールのサイズを変更", "拖动调整模块大小", "Drag to resize module" },
            ["AudioError"] = new[] { "システム音声を取得できませんでした。\n", "无法获取系统声音。\n", "Could not access system audio.\n" }
            , ["StartupError"] = new[] { "自動起動の設定を変更できませんでした。\n", "无法更改开机启动设置。\n", "Could not change the startup setting.\n" }
            , ["SaveSettingsError"] = new[] { "設定を保存できませんでした: ", "无法保存设置：", "Could not save settings: " }
            , ["DesktopNotFound"] = new[] { "デスクトップフォルダーが見つかりません。", "未找到桌面文件夹。", "Desktop folder was not found." }
            , ["DesktopLoadError"] = new[] { "デスクトップを読み込めませんでした: ", "无法读取桌面：", "Could not load the desktop: " }
            , ["EmptyDesktop"] = new[] { "デスクトップは空です", "桌面为空", "The desktop is empty" }
            , ["LoadedItems"] = new[] { " 件の項目を読み込みました", " 个项目已加载", " items loaded" }
            , ["SelectItemIcon"] = new[] { "個別アイコン画像を選択…", "选择自定义图标图片…", "Choose a custom icon image…" }
            , ["AdjustItemIcon"] = new[] { "画像のサイズ・位置を調整…", "调整图标图片大小和位置…", "Adjust icon image size and position…" }
            , ["ResetItemIcon"] = new[] { "既定のアイコンに戻す", "恢复默认图标", "Reset to default icon" }
            , ["SelectItemIconTitle"] = new[] { "項目のアイコン画像を選択", "选择项目图标图片", "Choose item icon image" }
            , ["ImageFiles"] = new[] { "画像ファイル", "图片文件", "Image files" }
            , ["AllFiles"] = new[] { "すべてのファイル", "所有文件", "All files" }
            , ["ItemIconLoadError"] = new[] { "アイコン画像を読み込めませんでした: ", "无法读取图标图片：", "Could not load icon image: " }
            , ["ItemIconDialogTitle"] = new[] { "アイコン画像を調整", "调整图标图片", "Adjust icon image" }
            , ["HorizontalPosition"] = new[] { "左右位置", "水平位置", "Horizontal position" }
            , ["VerticalPosition"] = new[] { "上下位置", "垂直位置", "Vertical position" }
            , ["CategoryExists"] = new[] { "同じ名前のカテゴリーがすでにあります。", "已存在同名分类。", "A category with that name already exists." }
            , ["CategoryNameDuplicate"] = new[] { "カテゴリー名は重複できません。", "分类名称不能重复。", "Category names must be unique." }
            , ["RefreshDone"] = new[] { "デスクトップを更新しました · 変更は自動保存されます", "桌面已刷新 · 更改会自动保存", "Desktop refreshed · changes are saved automatically" }
            , ["SelectCategoryName"] = new[] { "カテゴリーを追加", "添加分类", "Add category" }
            , ["CategoryName"] = new[] { "カテゴリー名", "分类名称", "Category name" }
            , ["SelectCategoryIconTitle"] = new[] { "カテゴリー画像を選択", "选择分类图标", "Choose category image" }
            , ["SelectCategoryBackgroundTitle"] = new[] { "カテゴリーの背景画像を選択", "选择分类背景图片", "Choose category background image" }
            , ["BackgroundLoadError"] = new[] { "背景画像を読み込めませんでした: ", "无法读取背景图片：", "Could not load background image: " }
            , ["CategoryImageLoadError"] = new[] { "画像を読み込めませんでした: ", "无法读取图片：", "Could not load image: " }
            , ["EnterCategoryName"] = new[] { "カテゴリー名を入力してください。", "请输入分类名称。", "Enter a category name." }
            , ["Decision"] = new[] { "決定", "确定", "OK" }
            , ["CategoryIconTitle"] = new[] { "カテゴリー画像", "分类图标", "Category image" }
            , ["BackgroundImageSuffix"] = new[] { "背景画像", "背景图片", "Background image" }
            , ["DeleteCategoryQuestion"] = new[] { "カテゴリーを削除", "删除分类", "Delete category" }
        };

        private static readonly string[] LanguageCodes = { "ja", "zh-CN", "en" };
        private static readonly string[] LanguageNames = { "日本語", "简体中文", "English" };
        private static int _index;
        internal static event EventHandler? LanguageChanged;
        internal static string CurrentCode => LanguageCodes[_index];
        internal static string CurrentName => LanguageNames[_index];

        internal static string Get(string key, params object[] args)
        {
            if (!Texts.TryGetValue(key, out var values)) return key;
            var value = values[_index];
            return args.Length == 0 ? value : string.Format(value, args);
        }

        internal static string Get(string key, string languageCode)
        {
            var index = Array.IndexOf(LanguageCodes, languageCode);
            return Texts.TryGetValue(key, out var values) ? values[index < 0 ? 0 : index] : key;
        }

        internal static void SetLanguage(string languageCode)
        {
            var index = Array.IndexOf(LanguageCodes, languageCode);
            _index = index < 0 ? 0 : index;
            if (Application.Current != null)
                foreach (var pair in Texts) Application.Current.Resources[pair.Key] = pair.Value[_index];
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
