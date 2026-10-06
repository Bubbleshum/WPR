// Unicorn's register and protection numbers, for builds that carry no Unicorn at all.
//
// The probe speaks UC_ARM_REG_* numbering throughout (see IArmCpu), and DynarmicArmCpu maps it.
// The values are copied from UnicornEngine.Unicorn 2.1.3 - read out of that assembly by
// reflection, not retyped - so a build without the package numbers registers identically.
// This file is compiled only by WPR.Wp8Runtime.csproj, where WPR_NO_UNICORN is defined; with the
// package referenced these names come from UnicornEngine.Const and this would collide.
// Unicorn is GPLv2 and must never ship (see the unicorn-is-gpl-wpr-is-mit note), which is the
// whole reason this exists: numbers are not code, the binding is.
namespace UnicornEngine.Const;

internal static class Arm
{
    public const int UC_ARM_REG_C13_C0_2 = 112;
    public const int UC_ARM_REG_C13_C0_3 = 113;
    public const int UC_ARM_REG_C1_C0_2 = 111;
    public const int UC_ARM_REG_CPSR = 3;
    public const int UC_ARM_REG_CP_REG = 139;
    public const int UC_ARM_REG_FPEXC = 4;
    public const int UC_ARM_REG_FPSCR = 6;
    public const int UC_ARM_REG_LR = 10;
    public const int UC_ARM_REG_PC = 11;
    public const int UC_ARM_REG_R0 = 66;
    public const int UC_ARM_REG_R1 = 67;
    public const int UC_ARM_REG_R2 = 68;
    public const int UC_ARM_REG_R3 = 69;
    public const int UC_ARM_REG_R4 = 70;
    public const int UC_ARM_REG_R5 = 71;
    public const int UC_ARM_REG_R6 = 72;
    public const int UC_ARM_REG_R7 = 73;
    public const int UC_ARM_REG_R8 = 74;
    public const int UC_ARM_REG_R9 = 75;
    public const int UC_ARM_REG_R10 = 76;
    public const int UC_ARM_REG_R11 = 77;
    public const int UC_ARM_REG_R12 = 78;
    public const int UC_ARM_REG_SP = 12;
}

internal static class Common
{
    public const int UC_ARCH_ARM = 1;
    public const int UC_HOOK_MEM_PROT = 896;
    public const int UC_HOOK_MEM_UNMAPPED = 112;
    public const int UC_MEM_FETCH_UNMAPPED = 21;
    public const int UC_MEM_READ_PROT = 23;
    public const int UC_MEM_READ_UNMAPPED = 19;
    public const int UC_MEM_WRITE_PROT = 22;
    public const int UC_MEM_WRITE_UNMAPPED = 20;
    public const int UC_MODE_THUMB = 16;
    public const int UC_PROT_ALL = 7;
    public const int UC_PROT_EXEC = 4;
    public const int UC_PROT_READ = 1;
    public const int UC_PROT_WRITE = 2;
    public const int UC_PROT_NONE = 0;
    public const int UC_MEM_FETCH_PROT = 24;
}
