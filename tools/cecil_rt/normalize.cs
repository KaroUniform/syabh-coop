// Нормализация сборки после MonoMod для старого Mono из Unity 4.6.
//   mono normalize.exe <вход.dll> <выход.dll>
//
// Проблема (2026-11-23): MonoMod 22.7 переписывает обращения к СОБСТВЕННЫМ
// полям и методам сборки как MemberRef с родителем TypeDef (у нас 48 523 таких
// ссылки против 9 687 у чистой пересохранки Cecil). Старый Mono из Unity 4.6
// на этом ломается: тысячи TypeLoadException при загрузке сцены. Чистая
// пересохранка Cecil 0.11.4 без MonoMod — работает.
// Решение: заменить такие ссылки прямыми определениями (MethodDef/FieldDef) и
// убрать служебный тип MonoMod.WasHere.
using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class Normalize
{
    static ModuleDefinition M;
    static int Fixed;

    static bool IsOwn(TypeReference t)
    {
        if (t == null || t is TypeSpecification) return false; // generic instance и пр. — оставляем
        var d = t.Resolve();
        return d != null && d.Module == M;
    }

    static MethodReference Fix(MethodReference mr)
    {
        if (mr == null || mr is MethodDefinition) return mr;
        if (mr is GenericInstanceMethod gim)
        {
            var el = Fix(gim.ElementMethod);
            if (el == gim.ElementMethod) return mr;
            var ng = new GenericInstanceMethod(el);
            foreach (var a in gim.GenericArguments) ng.GenericArguments.Add(a);
            return ng;
        }
        if (!IsOwn(mr.DeclaringType)) return mr;
        var def = mr.Resolve();
        if (def == null || def.Module != M) return mr;
        Fixed++;
        return def;
    }

    static FieldReference Fix(FieldReference fr)
    {
        if (fr == null || fr is FieldDefinition) return fr;
        if (!IsOwn(fr.DeclaringType)) return fr;
        var def = fr.Resolve();
        if (def == null || def.Module != M) return fr;
        Fixed++;
        return def;
    }

    static void FixAttrs(ICustomAttributeProvider p)
    {
        if (!p.HasCustomAttributes) return;
        foreach (var ca in p.CustomAttributes)
        {
            var c = Fix(ca.Constructor);
            if (c != ca.Constructor) ca.Constructor = c;
        }
    }

    static void Main(string[] a)
    {
        var r = new DefaultAssemblyResolver();
        r.AddSearchDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(a[0])));
        var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters { AssemblyResolver = r });
        M = asm.MainModule;
        FixAttrs(asm);
        FixAttrs(M);
        // Члены своих generic-типов в конкретной подстановке (GenericInstanceType)
        // намеренно не трогаем: на них и компилятор C# всегда ссылается через
        // MemberRef с родителем TypeSpec, заменить определением нельзя (ревью
        // этапа 0: Kimi считала это пропуском, Qwen — нормой; права Qwen).
        foreach (var t in M.GetTypes().ToList())
        {
            FixAttrs(t);
            foreach (var g in t.GenericParameters) FixAttrs(g);
            foreach (var f in t.Fields) FixAttrs(f);
            foreach (var p in t.Properties) FixAttrs(p);
            foreach (var e in t.Events) FixAttrs(e);
            foreach (var m in t.Methods)
            {
                FixAttrs(m);
                FixAttrs(m.MethodReturnType);
                foreach (var prm in m.Parameters) FixAttrs(prm);
                foreach (var g in m.GenericParameters) FixAttrs(g);
                for (int i = 0; i < m.Overrides.Count; i++) m.Overrides[i] = Fix(m.Overrides[i]);
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.Operand is MethodReference mr) ins.Operand = Fix(mr);
                    else if (ins.Operand is FieldReference fr) ins.Operand = Fix(fr);
                }
            }
        }
        // Защита: метод без тела (RVA 0, не abstract/PInvoke/рантайм) значит, что
        // объявление-ссылка из патча без [MonoModIgnore] заменило метод игры пустым
        // (MonoMod снимает флаг InternalCall, но тела нет — проверено 2026-11-23).
        var bodiless = M.GetTypes().SelectMany(t => t.Methods)
            .Where(m => (m.RVA == 0 || !m.HasBody) && !m.IsAbstract && !m.IsPInvokeImpl && !m.IsRuntime)
            .Select(m => m.FullName).ToList();
        if (bodiless.Count > 0)
        {
            foreach (var b in bodiless) Console.Error.WriteLine("normalize: ERROR method without body: " + b);
            Environment.Exit(3);
        }
        var was = M.Types.FirstOrDefault(t => t.FullName == "MonoMod.WasHere");
        if (was != null) M.Types.Remove(was);
        asm.Write(a[1]);
        Console.WriteLine($"normalize: {Fixed} self member refs replaced with definitions; WasHere {(was != null ? "removed" : "absent")} -> {a[1]}");
    }
}
