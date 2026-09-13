<p align="center"><img src="assets/app_logo.png" width="72" alt=""></p>

# Mingal Tunnel

Split tunneling por app pra Windows: você marca quais programas passam pela VPN (Discord, Brave, um jogo…) e o resto do PC continua na conexão normal. Funciona com qualquer VPN WireGuard (Proton VPN free, Mullvad, servidor próprio), inclusive as que não têm split tunneling nativo.

Sucessor do [discord-tunneling](https://github.com/himingal/discord-tunneling), agora com janela, lista de apps e sem mexer em atalho nenhum.

## Como funciona

```
.conf WireGuard ─► perfil (chave criptografada com DPAPI)
apps marcados   ─► tunneled-apps.json (rule-set que o sing-box recarrega sozinho)
                           │
             config via stdin (a chave nunca vai pro disco)
                           ▼
                sing-box (processo filho)
                 ├─ adaptador TUN "MingalTunnel" (auto_route)
                 ├─ app do rule-set?  ─► WireGuard ─► internet pela VPN
                 └─ o resto           ─► placa de rede física ─► internet normal
```

- **Por processo, TCP e UDP.** O sing-box descobre o processo dono de cada conexão pelas tabelas do Windows. Por isso voz, vídeo e Go Live do Discord (WebRTC/UDP) também passam pelo túnel, o que um proxy SOCKS5 com `--proxy-server` não fazia.
- **Sobrevive a auto-updates.** Apps Squirrel (Discord, Slack) casam por `...\Discord\app-*\Discord.exe`, e apps da Store casam por qualquer versão do pacote.
- **Aplicação na hora.** Marcar ou desmarcar um app reescreve o rule-set e o sing-box recarrega sozinho, sem reiniciar o túnel nem derrubar conexões.
- **Kill-switch opcional, por app.** Enquanto o túnel não está conectado, o Windows Firewall bloqueia o app.
- **IPv6.** Se a sua rede tem IPv6 e o perfil VPN não tem, o IPv6 dos apps tunelados é recusado (eles caem pro IPv4 na hora) em vez de vazar por fora.

## Instalar

Baixe `MingalTunnel-Setup-x.y.z.exe` em Releases, instale, abra, importe o `.conf` na aba **Perfis VPN** e ligue o túnel. O Discord já vem marcado.

Onde pegar um `.conf` do Proton: account.protonvpn.com → Downloads → WireGuard configuration.

Se o Discord Tunneling antigo estiver instalado, o Mingal Tunnel oferece importar o perfil dele e desativar a inicialização antiga (os dois túneis ao mesmo tempo brigariam pela rota padrão).

## Limitações conhecidas

- **O túnel vale pro processo inteiro.** Marcar o Brave manda o navegador todo, não uma aba.
- **Apps WebView2** (novo Teams, WhatsApp Desktop, novo Outlook) fazem a rede pelo `msedgewebview2.exe`, que é compartilhado entre eles. Tunelar esse executável leva todos juntos.
- **Precisa de Administrador** pra criar o adaptador TUN. O autostart usa uma tarefa agendada, então não aparece UAC no logon.
- **Todo o tráfego IPv4 do PC passa pelo sing-box**, mesmo o que sai direto. É o custo de rotear por processo. A sobrecarga é pequena, mas não é zero.
- **Conexões abertas antes de marcar/desmarcar** continuam onde começaram até o app reconectar. O botão **Reaplicar** fecha as que estão do lado errado e o **Reabrir** reinicia o app.

## Testando se está funcionando

1. Ligue o túnel e clique em **Verificar**. O IP "Pela VPN" tem que ser diferente do "Normal".
2. Abra o Discord pelo Mingal (ou clique em **Reabrir**). A coluna "Agora" deve mostrar `No túnel · N conexões`.
3. Num app tunelado, abra https://ipleak.net: deve aparecer o IP e o país da VPN. Num app não tunelado, o seu IP normal.
4. Desligue o túnel com o kill-switch marcado no app: ele tem que ficar sem internet.
5. Troque de rede (cabo ↔ Wi-Fi) com o túnel ligado: o log mostra "Rede mudou…" e o túnel volta sozinho.

## Build

Requer .NET SDK 10 e Inno Setup 6.

```powershell
build\fetch-deps.ps1    # baixa sing-box 1.14.0 e wintun 0.14.1 (SHA256 fixos)
build\publish.ps1       # testes + publish self-contained + instalador em installer\output
```

Em Debug o app roda sem elevação (a interface funciona, mas o túnel não liga). `MingalTunnel.exe --snapshot <pasta>` renderiza as abas em PNG.

## Privacidade

Tudo fica em `%LOCALAPPDATA%\MingalTunnel`. Não tem telemetria nem checagem de update. O app só faz tráfego próprio em duas situações: o túnel que você configurou e o "Verificar IP" (api.ipify.org), quando você clica. A saúde da VPN é testada com um `generate_204` da Cloudflare, feito pelo próprio túnel.

---

made by mingal
