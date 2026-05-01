ENTRY(main)
SECTIONS
{
  .text 0x40370000 :
  {
    *(.literal .literal.*)
    *(.text .text.*)
  }
  .rodata :
  {
    *(.rodata .rodata.*)
  }
  .data 0x3FC80000 :
  {
    *(.data .data.*)
    *(.bss .bss.*)
    *(COMMON)
  }
}
