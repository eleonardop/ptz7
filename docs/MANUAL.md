# PTZ Studio — Manual (português)

## Instalação
1. Execute **PTZStudio-Setup.exe** (ou abra direto o **PTZStudio.exe**, que não precisa de instalação).
2. Não é necessário instalar Python, .NET ou qualquer outra ferramenta.

## Primeiros passos
1. Aba **Câmera**: informe o IP da câmera (LJ510-30X-N; só números, como 192.168.1.100) e clique em **DESCOBRIR PORTA E PROTOCOLO**: o programa testa as portas 1259 e 52381 nos dois protocolos e preenche o que a câmera respondeu. Depois clique em **USAR ESTA CÂMERA, CONECTAR E TESTAR**. Se nada responder, confira o IP, o cabo/Wi-Fi, o firewall e o manual da câmera.
2. Conecte o GameSir (Bluetooth ou USB). O topo da janela mostra o nome do controle quando ele é detectado.
3. Aba **Controle PTZ**: teste com o joystick virtual (mouse) e com o controle físico. Os dois funcionam ao mesmo tempo, sem precisar escolher.
4. Aba **Analógicos**: ative **Pan/Tilt completo (Analógico)** e use **Calibrar automaticamente** para conferir a direção.
5. Aba **Mapeamento**: clique em **ASSISTENTE: MAPEAR TODOS OS BOTÕES** e aperte cada botão quando o programa pedir. As funções já atribuídas continuam nos mesmos botões do desenho.

## Cabeçalho (sempre visível)
Mostra o estado da câmera, do controle, o perfil e quem está no comando agora: **Controle**, **Mouse** ou **Controle + mouse** (nenhum dos dois em uso, ambos disponíveis).

Abaixo do cabeçalho ficam duas áreas que acompanham você em qualquer aba: a **faixa de alerta** (só aparece quando há um problema que precisa de atenção) e a **barra de presets**.

## Controle físico e joystick virtual ao mesmo tempo
O GameSir e o joystick virtual da tela (arrastado com o mouse) comandam a câmera **ao mesmo tempo**. Não existe mais botão, função nem opção para alternar entre eles.
- Para o movimento continuar proporcional, as duas entradas **nunca se somam**: quem saiu do centro primeiro **mantém o comando até voltar ao centro**. Enquanto isso, a outra entrada é ignorada. Se as duas saírem do centro no mesmo instante, vale o controle físico.
- Uma pequena folga do analógico em repouso (dentro da zona morta do perfil) não conta como uso e não impede o mouse de assumir.
- **Clicar no joystick virtual (ou nos botões de zoom da tela) sempre retoma o comando**, mesmo que o controle físico esteja inclinado ou travado: nesse caso o controle físico é ignorado até voltar ao centro.
- O botão do GameSir e as funções dele (presets, Home, foco, brilho etc.) funcionam como sempre, a qualquer momento.
- O programa não move o cursor do Windows com os analógicos: o "mouse" aqui é o joystick virtual da tela. O cartão **Entradas**, na aba Controle PTZ, mostra quem está no comando.
- Perfis e configurações antigas continuam valendo: a função "Alternar joystick físico/virtual", que deixou de existir, é removida das atribuições ao abrir o programa (o botão View, que a usava por padrão, fica livre para outra função).

## Parar a câmera
Não existe botão de parada de emergência nem “retomar controle”. A câmera para como sempre: **ao soltar o analógico ou o joystick virtual** (a parada é enviada na hora). Como a rede UDP pode perder pacotes, a parada é **repetida por cerca de 5,5 segundos** depois de soltar. O botão “Parar” (aba Mapeamento, por padrão no botão X do controle) faz uma parada imediata.

Proteções automáticas, para a câmera não ficar andando sozinha:
- **Guardião:** uma vigilância independente da tela. Se o ciclo de controle travar (por exemplo, o programa congelar) com a câmera em movimento, o guardião manda parar em cerca de meio segundo. Depois disso, a câmera só volta a se mover quando os controles passarem pelo centro.
- **Queda e retorno da rede ou da câmera:** quando a câmera volta a responder (e também na primeira conexão ao abrir o programa), o PTZ Studio reenvia a parada, se a câmera não estiver sendo movida por você nem indo a um preset. Isso cobre a parada que se perdeu durante a queda.
- **Encerramento inesperado:** se o programa fechar por uma falha, ele tenta mandar parar antes de sair. Não há como fazer isso se o Windows encerrar o processo à força ou se ocorrer uma falha grave fora do controle do programa.
- **Controle desconectado, câmera sem resposta ou erro interno:** parada de segurança; a câmera só volta a se mover depois que os controles voltarem ao centro.

## Reconexão automática
Se a câmera deixa de responder, o programa tenta recuperar a conexão sozinho. Com as configurações padrão, **cerca de 7 segundos** sem resposta já mostram “Desconectado” (são até 3 verificações seguidas; o número é ajustável de 1 a 3 na aba Câmera). Ele **recria a conexão de rede** nessas falhas, quando o Windows avisa que a rede mudou e quando um envio falha. A faixa vermelha mostra “CÂMERA SEM RESPOSTA” com o botão **Reconectar**. Se a câmera mudou de endereço (IP), a faixa lembra de usar **Procurar câmera na rede**. Ao **suspender e retomar o Windows**, o programa para a câmera ao suspender e reconecta sozinho ~3 s depois de acordar.

## Faixa de alerta
Uma faixa fixa abaixo do cabeçalho, que **não some sozinha**, avisa (nesta ordem de prioridade): detecção de botão ou eixo ativa; câmera sem resposta ou conectando; controle diferente do mapeado; parada de segurança aguardando o centro; problema ao ler as configurações ao abrir o programa; **endereço, porta ou protocolo da câmera alterados e ainda não aplicados** (botão “Aplicar e reconectar”); **erro inesperado** do programa (botão “Entendi”; não abre mais janela que trava o trabalho). Quando existe uma ação útil, ela vem como botão na própria faixa.

## Barra de presets
Os presets da câmera ativa ficam numa barra fixa abaixo do cabeçalho, visíveis em qualquer aba e sem rolar a tela. Um clique aciona o preset. A ordem e os nomes são os da aba Presets.

## Home e Salvar preset: segurar o botão
Para evitar toque acidental, **Home** e **Salvar preset** só são executados depois de **segurar o botão por meio segundo**. Vale para o botão do controle (a barra inferior mostra “Segure para…” com o andamento) e para o botão **Posição inicial (Home)** da aba Controle PTZ. Acionar preset, parar, foco e as demais funções continuam instantâneos.

## Velocidade Pan/Tilt e Zoom (aba Controle PTZ)
- **Velocidade Pan/Tilt:** controle deslizante contínuo de **1% a 100%**, valendo para o joystick físico e para o virtual. Cada **perfil guarda a sua própria velocidade**; ao trocar de perfil o controle se atualiza. A tela mostra a velocidade real enviada à câmera (a câmera só tem 24 passos de Pan e 20 de Tilt, e 1% sempre equivale ao passo mais lento).
- **Intensidade do zoom:** de 1% a 100% da velocidade máxima de zoom do perfil, para os botões Aproximar/Afastar da tela. O zoom **não** é afetado pela velocidade Pan/Tilt. A câmera tem só 8 níveis de zoom, então a tela mostra o nível real. Essa intensidade vale quando o joystick virtual está no comando; o zoom do controle físico não é reduzido por ela.
- O joystick virtual volta sozinho ao centro quando você solta o mouse/dedo, troca de janela ou de aba, e a câmera para.

## Imagem da câmera: brilho e exposição
- Card **Imagem da câmera** (Controle PTZ). Ao conectar, o programa **pergunta à câmera** quais ajustes ela suporta; só os confirmados aparecem. Se a câmera não responder, nada é enviado e a tela explica (botão **Detectar ajustes de imagem** para tentar de novo).
- **Brilho** = o “ajuste de vídeo” da câmera (comando VISCA 04 A1, valor absoluto). **Exposição** = compensação de exposição (VISCA 04 3E/0E/4E, de −7 a +7). Cada um tem − / + (segure para repetir), controle deslizante e ↺ (volta ao valor de quando foi detectado).
- No **Mapeamento** existem as funções **Brilho +/−** e **Exposição +/−**; segure o botão para ajustar e solte para parar. Tela e controle mexem nos mesmos valores.
- Não implementados: ganho, obturador, íris e modo de exposição (automático/manual). Mudar o modo alteraria o comportamento da câmera ao vivo; podem ser acrescentados se você quiser.
- O suporte na LJ510-30X-N não pôde ser verificado sem a câmera: o fabricante informa brilho, íris e obturador automático/manual, mas não publica a lista de comandos. Por isso o programa confirma tudo com a própria câmera.

## Gatilhos RT e LT: zoom proporcional à pressão
Quando o **RT** (ou o **LT**) é mapeado como **Zoom aproximar** / **Zoom afastar** (aba Mapeamento, selecione o gatilho no desenho e escolha a função), o programa usa a **leitura analógica de pressão** do gatilho: aperto leve = zoom lento, aperto total = zoom na velocidade máxima do perfil. **Ao soltar o gatilho a parada de zoom é enviada no mesmo instante**, sem rampa, mesmo que “Parada suave” esteja ligada.
- A velocidade segue a **curva e a zona morta do zoom** do perfil (aba Movimento). Para uma resposta mais “direta” à pressão, escolha a curva **Linear**; com a curva padrão (progressiva) o início do curso fica mais fino.
- Um aperto muito leve (abaixo da zona morta do zoom) não move a câmera.
- A câmera tem só 8 velocidades de zoom; a velocidade máxima do zoom é a do perfil.
- Para isso funcionar, o gatilho precisa ter sido detectado como **eixo** (DETECTAR BOTÃO mostra “Eixo … (+/−)”). Se o Windows entregar o gatilho só como botão liga/desliga, ele continua funcionando, mas sempre na mesma velocidade.
- Se o gatilho estiver apertado quando você aciona um preset ou o Home, ele é ignorado até soltar.

## Foco e autofoco (aba Controle PTZ)
Card **FOCO**, entre o zoom e a imagem:
- **AUTOFOCO: LIGADO / DESLIGADO** — um botão que liga ou desliga o autofoco. O programa **pergunta à câmera** o modo real antes de trocar (se alguém mexeu pelo controle remoto da câmera, o botão não fica errado). Se a câmera não responder à pergunta, aparece “(não confirmado)”. Depois de trocar, ele confere de novo e avisa se a câmera não aceitou.
- **Focar agora (1 toque)** — a câmera foca uma vez e mantém. Isso desliga o autofoco contínuo (a câmera passa para foco manual; o aviso aparece na tela).
- **Foco perto / Foco longe** — segure para ajustar à mão; solte para parar.
- No **Mapeamento** há as funções **Alternar autofoco** e **Focar agora (1 toque)**.
Atenção: se a LJ510-30X-N não aceitar a consulta ou o foco de 1 toque, o programa avisa na tela; isso só dá para confirmar com a câmera real.

## Presets e Home: o preset ganha do analógico
Ao acionar um preset ou o Home com o analógico inclinado, o analógico é **ignorado até voltar ao centro** (a barra de status mostra “Indo para preset N…”). Sem isso, o movimento manual cortaria a viagem no meio. A zona morta do perfil define o que é “centro”. Se a câmera **recusar** um preset (número que ela não tem), o programa avisa; ajuste **Maior número de preset que a câmera aceita** na aba Câmera.

## Procurar câmera na rede (aba Câmera)
Botão **Procurar câmera na rede (ONVIF)**: lista câmeras que respondem à descoberta ONVIF, com o botão “Usar IP”. Nem toda câmera responde. Recomenda-se IP fixo (ou reserva no roteador).

## Vídeo ao vivo
O cartão **VÍDEO AO VIVO** da aba Controle PTZ mostra a câmera por RTSP, com tela cheia. Veja o guia completo em `VIDEO_AO_VIVO.md`.

## Pan/Tilt completo (Analógico)
Aba **Analógicos**. Um único analógico controla Pan e Tilt ao mesmo tempo: cima/baixo = Tilt, esquerda/direita = Pan, diagonais movem os dois, centro = para. Quanto mais inclinado, mais rápido, dentro dos limites que você definir. Ajustes: zona morta, sensibilidade, velocidade máxima de Pan e de Tilt (independentes), inversão dos eixos X e Y, aceleração e desaceleração. As opções separadas de Pan e Tilt continuam nas abas Analógicos e Sensibilidade.

## Mapeamento do controle
- Escolha **Frente** ou **Traseira**, clique no botão do desenho, escolha a função e clique em **Aplicar**.
- O desenho foi traçado a partir das fotos do GameSir Nova 2 Lite: analógicos, direcional, ABXY, LB/RB, LT/RT, View, Menu, Home, Screenshot, M e, na traseira, L4 e R4.
- Perfis já existentes não são alterados, exceto que as funções “Parada de emergência”, “Alternar nível de velocidade” e “Alternar joystick físico/virtual”, que deixaram de existir, são removidas das atribuições.
- Botões com função mostram um **selo azul** e uma etiqueta com o nome da função; botões pressionados acendem em verde; o ponto azul mostra a posição do analógico.
- **DETECTAR BOTÃO**: selecione o botão no desenho, clique em DETECTAR BOTÃO e aperte o botão físico. Faça isso para M, Screenshot, Home, L4 e R4, que não têm número fixo no Windows. A detecção (de botão ou de eixo) **termina sozinha em 10 segundos** e também ao sair da aba; enquanto ela está ativa, uma faixa avisa e as funções dos botões ficam suspensas (a detecção de eixo também suspende o analógico).
- **Controle diferente do mapeado:** os números dos botões mudam conforme o modo de conexão do GameSir (USB, Bluetooth). O programa guarda para qual controle (VID:PID) o mapeamento foi feito. Se outro controle ou outro modo for conectado, as funções dos botões ficam **suspensas** (os analógicos continuam) e a faixa avisa. Refaça o mapeamento com o assistente ou clique em “Usar este controle assim mesmo”.
- L4, R4 e M só aparecem se o Windows os enxergar no modo de conexão em uso. Se não aparecerem, veja a aba **Diagnóstico**.

## Movimento
A aba **Movimento** reúne os ajustes numéricos de Pan, Tilt e Zoom (antes divididos entre Sensibilidade e Zoom). A velocidade Pan/Tilt do perfil (1% a 100%) fica na aba Controle PTZ. **Restaurar padrões deste perfil** volta só os ajustes de movimento do perfil ativo aos valores de fábrica (botões, eixos e presets não mudam). A tela avisa quando um ajuste pode atrapalhar, como zona morta muito baixa ou filtro de ruído muito alto; seus valores nunca são alterados sozinhos.

## Presets
O preset é gravado na **memória da câmera** por comando VISCA. O programa só marca como confirmado quando a câmera responde; antes disso aparece como “aguardando confirmação”.

**Acionar preset e Home são confirmados pela câmera:** se a câmera já confirmou comandos antes (resposta ACK) e a confirmação deste não chega, o comando é reenviado até 2 vezes; se a câmera responder “memória de comandos cheia”, o reenvio é rápido; se ainda assim não confirmar, um aviso aparece. Câmeras que nunca confirmam comandos não recebem reenvio nem alarme falso. A aba Diagnóstico mostra se a câmera confirma comandos.

## Ao lado do relógio (bandeja do Windows)
- Ao **minimizar**, o PTZ Studio some da barra de tarefas e fica como um ícone ao lado do relógio. **Tudo continua funcionando**: controle físico, joystick virtual, presets, reconexão e os ajustes de imagem.
- **Clique no ícone** para abrir de novo. Botão direito: Abrir e Sair. Passar o mouse mostra o estado da câmera e do controle.
- O botão **fechar (X)** também só esconde a janela, para evitar fechar sem querer durante a transmissão. Para sair de verdade: botão direito no ícone > **Sair do PTZ Studio**.
- Com a janela escondida, o programa avisa por um balão quando a câmera perde a conexão.
- Abrir o programa de novo enquanto ele está na bandeja apenas traz a janela original (só uma cópia pode controlar a câmera). Isso vale entre usuários e sessões do Windows (acesso remoto, troca rápida de usuário): se a outra cópia estiver em outra sessão, o programa avisa em vez de abrir uma segunda.
- Para mudar esse comportamento: aba **Backup > Janela e bandeja do Windows**.

## Abertura
O programa abre a janela primeiro e só depois liga o controle e a câmera; as abas são criadas quando você as abre; a procura do controle e o backup automático rodam em segundo plano. A aba **Diagnóstico** mostra o tempo de abertura medido no seu computador.

## Desempenho
O programa foi feito para ser leve: com a janela escondida ou minimizada, as telas param de se atualizar; o controle dos movimentos tem prioridade sobre o desenho da tela; o processo roda com prioridade um pouco acima do normal (sem atrapalhar o OBS) e fora do “modo de economia” do Windows 11, que deixaria os movimentos irregulares quando minimizado.

## Registros
O programa **não** mostra nem guarda histórico de comandos (TX/RX) e não grava arquivos de registro contínuos. Na aba **Diagnóstico** ficam apenas os últimos erros de comunicação (na memória), quantas vezes a conexão foi recriada, se a câmera confirma comandos, para qual controle o mapeamento foi feito, e o botão **Copiar diagnóstico**, para pedir ajuda. Só falhas inesperadas do próprio programa vão para um pequeno arquivo `erros.log` (limite de 200 KB).

## Windows 7
O programa foi feito para Windows 10 e 11. O .NET 10, que ele usa desde esta versão, **não é suportado no Windows 7** (e a versão anterior, com .NET 8, também não era, embora tenha aberto no seu PC). **Com o .NET 10 não há garantia nenhuma de que abra no Windows 7: teste antes de depender dele na transmissão.** Se não abrir, use o Windows 10 ou 11. Os ícones agora são desenhados pelo próprio programa (a fonte de ícones do Windows 10 não existe no Windows 7). Se possível, use o Windows 10 ou 11 na transmissão. Também confirme que o GameSir é reconhecido no Windows 7 (o Bluetooth do Windows 7 costuma ser limitado; prefira o cabo USB).

## Backup
Aba **Backup**: criar backup, restaurar, exportar e importar configurações. Um backup automático é feito ao abrir o programa. Ao criar backup ou exportar, se houver senha RTSP salva, o programa pergunta se ela deve ir junto (**sem a senha** é o mais seguro para enviar a outras pessoas).

**Se as configurações não puderem ser lidas ao abrir** (arquivo bloqueado por antivírus ou por programa de nuvem, ou corrompido), o programa tenta de novo, usa a cópia de segurança (.bak) e avisa na faixa de alerta. Se nada puder ser lido, ele abre com os padrões e **guarda o arquivo anterior** como `settings.corrompido-DATA.json` na pasta de configurações; se nem isso for possível, ele não grava nada naquela sessão, para não perder o arquivo. As gravações são feitas em arquivo temporário e trocadas de uma só vez.

## Onde ficam os dados
Pasta `%AppData%\PTZStudio` (configurações e backups). Suas configurações antigas continuam valendo: as novas opções entram com valores que não mudam o comportamento anterior.
