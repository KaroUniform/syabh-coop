// Общее для всего кооп-мода: версия, флаги запуска, журнал.
using System;
using UnityEngine;

public static class CoopMod
{
    // Версия мода. Дописывается к строке версии игры (см. Patches/WatermarkUpdater.cs),
    // поэтому сервер сам отключит клиента с другой версией мода или без мода.
    // Менять при каждой сборке, которую ставим на оба MacBook.
    public const string Version = "0.1.2";

    // -coopdebug    — отладочные команды (выдать фрагмент, телепорт и т.п.)
    // -coopautotest — сервер сам проходит сценарий этапа и пишет итог в журнал
    public static readonly bool DebugMode = HasArg("-coopdebug");
    public static readonly bool AutoTest = HasArg("-coopautotest");
    // -coopbot      — клиент-бот для проверок: сам жмёт «готов» в лобби и
    //                 соглашается на предложения напарника
    public static readonly bool Bot = HasArg("-coopbot");
    // сверить быструю карту регионов с оригиналом (долго: +80 с на остров)
    public static readonly bool CheckRegion = HasArg("-coopcheckregion");

    // -ключ N в командной строке (для автотеста), иначе def
    public static int ArgInt(string name, int def)
    {
        var a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++)
        {
            int v;
            if (a[i] == name && int.TryParse(a[i + 1], out v)) return v;
        }
        return def;
    }

    static bool HasArg(string name)
    {
        try
        {
            foreach (string a in Environment.GetCommandLineArgs())
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (Exception) { }
        return false;
    }

    public static void Log(string msg)
    {
        Debug.Log("[SYABH-COOP] " + Stamp() + msg);
    }

    public static void Warn(string msg)
    {
        Debug.LogWarning("[SYABH-COOP] " + Stamp() + "WARN " + msg);
    }

    // время от старта процесса — чтобы по журналу видеть длительность этапов
    static string Stamp()
    {
        return "[" + Time.realtimeSinceStartup.ToString("0.0") + "] ";
    }

    // Строки, пришедшие по сети от другой стороны, — перед записью в журнал:
    // обрезать и убрать управляющие символы, чтобы нельзя было подделать строки лога.
    public static string Clean(string s, int max = 64)
    {
        if (s == null) return "<null>";
        if (s.Length > max) s = s.Substring(0, max);
        var b = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) b.Append(char.IsControl(c) ? '?' : c);
        return b.ToString();
    }
}
