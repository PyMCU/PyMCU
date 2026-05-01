ENTRY(main)
SECTIONS
{
  /* IRAM: .text + literal pools */
  .text 0x400D0000 :
  {
    *(.literal .literal.*)
    *(.text .text.*)
  }
  /* DROM: read-only data (flash-mapped) */
  .rodata :
  {
    *(.rodata .rodata.*)
  }
  /* DRAM: read-write data + BSS */
  .data 0x3FFB0000 :
  {
    *(.data .data.*)
    *(.bss .bss.*)
    *(COMMON)
  }
}
