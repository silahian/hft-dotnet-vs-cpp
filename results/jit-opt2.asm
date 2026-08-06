; Assembly listing for method Bench.Core.OrderBookWorkload:RunOpt2(System.ReadOnlySpan`1[Bench.Core.BookEvent],System.Span`1[long],System.Span`1[long]):ulong (Tier0)
; Emitting BLENDED_CODE for generic X64 + VEX on Windows
; Tier0 code
; rbp based frame
; partially interruptible
; compiling with minopt

G_M000_IG01:                ;; offset=0x0000
       push     rbp
       sub      rsp, 80
       lea      rbp, [rsp+0x50]
       vxorps   xmm4, xmm4, xmm4
       vmovdqu  ymmword ptr [rbp-0x30], ymm4
       vmovdqa  xmmword ptr [rbp-0x10], xmm4
       mov      bword ptr [rbp+0x10], rcx
       mov      bword ptr [rbp+0x18], rdx
       mov      bword ptr [rbp+0x20], r8
 
G_M000_IG02:                ;; offset=0x0024
       mov      rax, bword ptr [rbp+0x10]
 
G_M000_IG03:                ;; offset=0x0028
       vmovdqu  xmm0, xmmword ptr [rax]
       vmovdqu  xmmword ptr [rbp-0x10], xmm0
 
G_M000_IG04:                ;; offset=0x0031
       mov      rax, bword ptr [rbp+0x18]
 
G_M000_IG05:                ;; offset=0x0035
       vmovdqu  xmm0, xmmword ptr [rax]
       vmovdqu  xmmword ptr [rbp-0x20], xmm0
 
G_M000_IG06:                ;; offset=0x003E
       mov      rax, bword ptr [rbp+0x20]
 
G_M000_IG07:                ;; offset=0x0042
       vmovdqu  xmm0, xmmword ptr [rax]
       vmovdqu  xmmword ptr [rbp-0x30], xmm0
 
G_M000_IG08:                ;; offset=0x004B
       lea      rcx, [rbp-0x10]
       lea      rdx, [rbp-0x20]
       lea      r8, [rbp-0x30]
       call     [Bench.Core.OrderBookWorkload:RunOpt2[Bench.Core.NullTrace](System.ReadOnlySpan`1[Bench.Core.BookEvent],System.Span`1[long],System.Span`1[long]):ulong]
       nop      
 
G_M000_IG09:                ;; offset=0x005E
       add      rsp, 80
       pop      rbp
       ret      
 
; Total bytes of code 100

; Assembly listing for method Bench.Core.OrderBookWorkload:RunOpt2[Bench.Core.NullTrace](System.ReadOnlySpan`1[Bench.Core.BookEvent],System.Span`1[long],System.Span`1[long]):ulong (Tier0-FullOpts)
; Emitting BLENDED_CODE for generic X64 + VEX on Windows
; Tier-0 switched to FullOpts code
; optimized code
; optimized using Synthesized PGO
; rsp based frame
; fully interruptible
; with Synthesized PGO: fgCalledCount is 100
; No PGO data
; 0 inlinees with PGO data; 21 single block inlinees; 2 inlinees without PGO data

G_M000_IG01:                ;; offset=0x0000
       push     rdi
       push     rsi
       push     rbp
       push     rbx
       sub      rsp, 40
       mov      rsi, rcx
       mov      rbx, r8
 
G_M000_IG02:                ;; offset=0x000E
       mov      rdi, bword ptr [rdx]
       mov      ebp, dword ptr [rdx+0x08]
       mov      edx, ebp
       shl      rdx, 3
       mov      rcx, rdi
       call     [System.SpanHelpers:ClearWithoutReferences(byref,nuint)]
       mov      edx, dword ptr [rbx+0x08]
       shl      rdx, 3
       mov      rcx, bword ptr [rbx]
       call     [System.SpanHelpers:ClearWithoutReferences(byref,nuint)]
       mov      eax, -1
       mov      ecx, ebp
       mov      rdx, 0xCBF29CE484222325
       mov      r8, bword ptr [rsi]
       mov      r10d, dword ptr [rsi+0x08]
       shl      r10, 4
       add      r10, r8
       mov      r9, bword ptr [rbx]
       cmp      r8, r10
       jb       G_M000_IG21
 
G_M000_IG03:                ;; offset=0x005E
       mov      rax, rdx
 
G_M000_IG04:                ;; offset=0x0061
       add      rsp, 40
       pop      rbx
       pop      rbp
       pop      rsi
       pop      rdi
       ret      
 
G_M000_IG05:                ;; offset=0x006A
       cmp      r11d, eax
       jne      G_M000_IG25
 
G_M000_IG06:                ;; offset=0x0073
       dec      eax
       js       SHORT G_M000_IG09
       align    [9 bytes for IG07]
 
G_M000_IG07:                ;; offset=0x0080
       movsxd   r11, eax
       cmp      qword ptr [rdi+8*r11], 0
       jne      SHORT G_M000_IG09
 
G_M000_IG08:                ;; offset=0x008A
       dec      eax
       jns      SHORT G_M000_IG07
 
G_M000_IG09:                ;; offset=0x008E
       mov      r11d, eax
       mov      eax, r11d
       jmp      G_M000_IG25
       align    [0 bytes for IG15]
 
G_M000_IG10:                ;; offset=0x0099
       movsxd   rsi, r11d
       mov      qword ptr [r9+8*rsi], rbx
       test     rbx, rbx
       je       SHORT G_M000_IG13
 
G_M000_IG11:                ;; offset=0x00A5
       cmp      r11d, ecx
       jge      G_M000_IG25
 
G_M000_IG12:                ;; offset=0x00AE
       mov      ecx, r11d
       jmp      G_M000_IG25
 
G_M000_IG13:                ;; offset=0x00B6
       cmp      r11d, ecx
       jne      G_M000_IG25
 
G_M000_IG14:                ;; offset=0x00BF
       inc      ecx
       cmp      ecx, ebp
       jge      SHORT G_M000_IG17
 
G_M000_IG15:                ;; offset=0x00C5
       movsxd   r11, ecx
       cmp      qword ptr [r9+8*r11], 0
       jne      SHORT G_M000_IG17
 
G_M000_IG16:                ;; offset=0x00CF
       inc      ecx
       cmp      ecx, ebp
       jl       SHORT G_M000_IG15
 
G_M000_IG17:                ;; offset=0x00D5
       jmp      SHORT G_M000_IG25
 
G_M000_IG18:                ;; offset=0x00D7
       xor      r11d, r11d
       jmp      SHORT G_M000_IG27
 
G_M000_IG19:                ;; offset=0x00DC
       xor      ebx, ebx
 
G_M000_IG20:                ;; offset=0x00DE
       movsxd   rsi, eax
       xor      rdx, rsi
       mov      rsi, 0x100000001B3
       imul     rdx, rsi
       xor      rdx, r11
       imul     rdx, rsi
       movsxd   r11, ecx
       xor      rdx, r11
       imul     rdx, rsi
       xor      rdx, rbx
       imul     rdx, rsi
       add      r8, 16
       cmp      r8, r10
       jae      G_M000_IG03
 
G_M000_IG21:                ;; offset=0x0117
       mov      r11d, dword ptr [r8]
       mov      rbx, qword ptr [r8+0x08]
       cmp      dword ptr [r8+0x04], 0
       jne      G_M000_IG10
 
G_M000_IG22:                ;; offset=0x0129
       movsxd   rsi, r11d
       mov      qword ptr [rdi+8*rsi], rbx
       test     rbx, rbx
       je       G_M000_IG05
 
G_M000_IG23:                ;; offset=0x0139
       cmp      r11d, eax
       jle      SHORT G_M000_IG25
 
G_M000_IG24:                ;; offset=0x013E
       mov      eax, r11d
 
G_M000_IG25:                ;; offset=0x0141
       test     eax, eax
       jl       SHORT G_M000_IG18
 
G_M000_IG26:                ;; offset=0x0145
       mov      r11d, eax
       mov      r11, qword ptr [rdi+8*r11]
 
G_M000_IG27:                ;; offset=0x014C
       cmp      ecx, ebp
       jge      SHORT G_M000_IG19
 
G_M000_IG28:                ;; offset=0x0150
       movsxd   rbx, ecx
       mov      rbx, qword ptr [r9+8*rbx]
       jmp      SHORT G_M000_IG20
 
; Total bytes of code 345

