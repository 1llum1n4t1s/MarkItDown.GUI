using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MarkItDown.GUI.ViewModels;

/// <summary>
/// INotifyPropertyChanged を実装した ViewModel の基底クラス
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <summary>
    /// プロパティ変更時に発生するイベント
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// プロパティの値を更新し、変更を通知する
    /// </summary>
    /// <typeparam name="T">プロパティの型</typeparam>
    /// <param name="field">バッキングフィールドへの参照</param>
    /// <param name="value">新しい値</param>
    /// <param name="propertyName">プロパティ名（省略時は呼び出し元メンバー名）</param>
    /// <returns>値が変更された場合は true</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// プロパティ変更を通知する
    /// </summary>
    /// <param name="propertyName">プロパティ名（省略時は呼び出し元メンバー名）</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
