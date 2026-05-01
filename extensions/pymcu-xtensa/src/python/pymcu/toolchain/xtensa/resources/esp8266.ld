ENTRY(main)
SECTIONS
{
  .text 0x40100000 :
  {
    *(.literal .literal.*)
    *(.text .text.*)
  }
  .rodata :
  {
    *(.rodata .rodata.*)
  }
  .data 0x3FFE8000 :
  {
    *(.data .data.*)
    *(.bss .bss.*)
    *(COMMON)
  }
}
