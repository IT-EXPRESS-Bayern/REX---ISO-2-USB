; A Linux boot-protocol 2.06 kernel that does nothing but report on the first serial port:
; the command line the boot loader handed over and the size of the initial ramdisk.
; It lets a test prove that a boot loader on a freshly written stick loads kernel and initrd
; and passes exactly the parameters that Bootrix put into the loader's configuration.
;
; Both entry points of the protocol are implemented: the real-mode one at 0x200 (Syslinux, GRUB's
; linux16) and the 32-bit one at code32_start (GRUB's linux, which most menus use).
;
;   nasm -f bin fakekernel.asm -o fakekernel.bin

bits 16
org 0

SETUP_SECTS     equ 1
PAYLOAD_BYTES   equ 1024

times 0x1F1 db 0

        db  SETUP_SECTS                 ; 0x1F1 setup_sects
        dw  1                           ; 0x1F2 root_flags
        dd  PAYLOAD_BYTES / 16          ; 0x1F4 syssize in paragraphs
        dw  0                           ; 0x1F8 ram_size
        dw  0xFFFF                      ; 0x1FA vid_mode
        dw  0                           ; 0x1FC root_dev
        dw  0xAA55                      ; 0x1FE boot_flag

; 0x200: the loader enters here in real mode with cs = segment + 0x20
        jmp short start                 ; 0x200 jump
        db  'HdrS'                      ; 0x202 header magic
        dw  0x0206                      ; 0x206 version
        dd  0                           ; 0x208 realmode_swtch
        dw  0                           ; 0x20C start_sys_seg
        dw  0                           ; 0x20E kernel_version
        db  0                           ; 0x210 type_of_loader
        db  0x01                        ; 0x211 loadflags: loaded high
        dw  0                           ; 0x212 setup_move_size
        dd  0x100000                    ; 0x214 code32_start
        dd  0                           ; 0x218 ramdisk_image
        dd  0                           ; 0x21C ramdisk_size
        dd  0                           ; 0x220 bootsect_kludge
        dw  0                           ; 0x224 heap_end_ptr
        db  0                           ; 0x226 ext_loader_ver
        db  0                           ; 0x227 ext_loader_type
        dd  0                           ; 0x228 cmd_line_ptr
        dd  0x7FFFFFFF                  ; 0x22C initrd_addr_max
        dd  0x200000                    ; 0x230 kernel_alignment
        db  0                           ; 0x234 relocatable_kernel
        db  0                           ; 0x235 min_alignment
        dw  0                           ; 0x236 xloadflags
        dd  255                         ; 0x238 cmdline_size

start:
        cli
        mov     ax, cs
        sub     ax, 0x20
        mov     ds, ax                  ; ds = start of the real-mode image, where the header lives
        mov     ss, ax
        mov     sp, 0x9000

        mov     si, banner
        call    puts

        ; command line: a linear address below 1 MiB
        mov     eax, [0x228]
        mov     bx, ax
        and     bx, 0x0F
        shr     eax, 4
        push    ds
        mov     ds, ax
        mov     si, bx
        call    puts
        pop     ds

        mov     si, crlf
        call    puts
        mov     si, initrd_text
        call    puts
        mov     eax, [0x21C]
        call    put_hex32
        mov     si, crlf
        call    puts

        ; ACPI power-off of QEMU's PIIX4, then stop
        mov     dx, 0x604
        mov     ax, 0x2000
        out     dx, ax
halt:
        hlt
        jmp     halt

; ds:si points at a zero-terminated string
puts:
        lodsb
        test    al, al
        jz      .done
        call    putc
        jmp     puts
.done:
        ret

put_hex32:
        mov     cx, 8
.next:
        rol     eax, 4
        push    ax
        and     al, 0x0F
        add     al, '0'
        cmp     al, '9'
        jbe     .digit
        add     al, 'A' - '9' - 1
.digit:
        call    putc
        pop     ax
        loop    .next
        ret

putc:
        push    dx
        push    ax
        mov     dx, 0x3FD
.wait:
        in      al, dx
        test    al, 0x20
        jz      .wait
        pop     ax
        mov     dx, 0x3F8
        out     dx, al
        pop     dx
        ret

banner:         db 'BOOTRIX-KERNEL cmdline=', 0
initrd_text:    db 'BOOTRIX-INITRD size=', 0
crlf:           db 13, 10, 0

; pad the real-mode part to 1 + SETUP_SECTS sectors; the protected-mode kernel follows
times (1 + SETUP_SECTS) * 512 - ($ - $$) db 0

; 32-bit entry at 0x100000 with esi pointing at the boot parameters (the "zero page")
payload:
bits 32
        cld
        call    .base
.base:  pop     ebp                     ; ebp = where this code really runs
        mov     edx, esi

        lea     esi, [ebp + banner32 - .base]
        call    puts32
        mov     esi, [edx + 0x228]      ; cmd_line_ptr
        call    puts32
        lea     esi, [ebp + crlf32 - .base]
        call    puts32
        lea     esi, [ebp + initrd32 - .base]
        call    puts32
        mov     eax, [edx + 0x21C]      ; ramdisk_size
        call    put_hex32_pm
        lea     esi, [ebp + crlf32 - .base]
        call    puts32

        mov     dx, 0x604
        mov     ax, 0x2000
        out     dx, ax
.stop:
        hlt
        jmp     .stop

puts32:
        lodsb
        test    al, al
        jz      .done
        call    putc32
        jmp     puts32
.done:
        ret

put_hex32_pm:
        mov     ecx, 8
.next:
        rol     eax, 4
        push    eax
        and     al, 0x0F
        add     al, '0'
        cmp     al, '9'
        jbe     .digit
        add     al, 'A' - '9' - 1
.digit:
        call    putc32
        pop     eax
        loop    .next
        ret

putc32:
        push    edx
        push    eax
        mov     dx, 0x3FD
.wait:
        in      al, dx
        test    al, 0x20
        jz      .wait
        pop     eax
        mov     dx, 0x3F8
        out     dx, al
        pop     edx
        ret

banner32:       db 'BOOTRIX-KERNEL cmdline=', 0
initrd32:       db 'BOOTRIX-INITRD size=', 0
crlf32:         db 13, 10, 0

times PAYLOAD_BYTES - ($ - payload) db 0xF4
