using System;
using System.Reflection;
using UnityEngine;

namespace FearNoSpear;

internal static class SpearChatCommand
{
    internal static bool TryConsume(Chat chat)
    {
        string command = FearNoSpearPlugin.Cfg.ChatCommand.Value.Trim();
        if (string.IsNullOrEmpty(command)) return false;

        string input = GetInputText(chat);
        if (!string.Equals(input.Trim(), command, StringComparison.OrdinalIgnoreCase)) return false;

        ClearInput(chat);
        SpearLocator.PinKnownSpear();
        return true;
    }

    private static string GetInputText(Chat chat)
    {
        object? input = ReflectionCache.Get<object?>(ReflectionCache.F_terminalInput, chat, null);
        if (input == null) return string.Empty;

        PropertyInfo? textProperty = input.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
        return textProperty?.GetValue(input, null) as string ?? string.Empty;
    }

    private static void ClearInput(Chat chat)
    {
        object? input = ReflectionCache.Get<object?>(ReflectionCache.F_terminalInput, chat, null);
        if (input == null) return;

        PropertyInfo? textProperty = input.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
        textProperty?.SetValue(input, string.Empty, null);

        if (input is Component component)
        {
            component.gameObject.SetActive(false);
        }

        chat.Hide();
    }
}
