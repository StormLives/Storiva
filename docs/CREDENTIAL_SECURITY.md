# Segurança das credenciais

## 1. O que é guardado, e onde

Tudo fica em `%LOCALAPPDATA%\Storiva\`, por usuário do Windows e sem *roaming*:

| Arquivo | Conteúdo | Segredo? |
| --- | --- | --- |
| `settings.json` | Formato, versão e tema (`System`/`Light`/`Dark`) | Não |
| `connections.json` | Por conexão: id, nome, provedor, endpoint, região, método de autenticação, nome do perfil AWS, path-style, HTTP permitido, checksums, último teste (estado, data e resumo gerado pelo Storiva) | **Não**: o arquivo não tem nenhum campo de chave, segredo ou token |
| `secrets\<id>.bin` | Access key ID, secret access key e session token, cifrados com **DPAPI** | Sim, cifrado |
| `*.corrupt-<data>` | Cópia de um arquivo que não pôde ser lido (mantida para diagnóstico) | Igual ao original |

Conexões com **perfil AWS** ou **cadeia padrão** não têm arquivo em `secrets\`: as credenciais ficam com as ferramentas da AWS (`%USERPROFILE%\.aws`, SSO), e o Storiva só as solicita ao SDK no momento de testar ou conectar.

## 2. Como a proteção funciona

- `System.Security.Cryptography.ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`: a chave de cifragem é derivada pelo Windows da conta do usuário. Só o **mesmo usuário no mesmo computador** consegue decifrar.
- **Entropia** adicional `Storiva.ConnectionSecret.v1:<id>`: um arquivo copiado para outra conexão não é decifrado.
- Gravação atômica (arquivo temporário + renomear). Os bytes em texto puro usados na cifragem e na decifragem são zerados logo depois (`CryptographicOperations.ZeroMemory`).
- Falha ao decifrar (outro usuário, outro PC, senha redefinida por um administrador, arquivo danificado): mensagem segura, sem o conteúdo do erro, pedindo para editar a conexão e digitar as credenciais de novo.

## 3. O que o Storiva faz para não expor segredos

- `StorageCredentials.ToString()` e `CredentialsDraft.ToString()` são redigidos.
- Mensagens de erro e linhas do Event log são escritas pelo Storiva. O texto do servidor só é anexado depois do `SecretRedactor`, que remove os valores conhecidos da conexão, os padrões de *access key ID* (`AKIA…`, `ASIA…`), os segredos de 40 caracteres e os pares `Signature=`/`Credential=`.
- O Event log registra nomes, caminhos e resultados; nunca objetos de configuração nem credenciais.
- O diálogo de edição não decifra o segredo para exibi-lo. Mostra só uma dica (`AKIA…WXYZ`) e "deixe vazio para manter". Os campos de segredo são mascarados e limpos ao fechar.
- Os segredos nunca aparecem na linha de comando nem em arquivos de exemplo. Os testes usam valores fictícios (`STORIVATESTKEY`).
- Os testes verificam que `connections.json` e os arquivos em `secrets\` não contêm o segredo em texto puro, em UTF-8 nem em UTF-16. O `tools\validate.ps1` repete a varredura na pasta de dados do smoke test.

## 4. Limites (leia antes de confiar)

- O DPAPI protege contra a leitura dos arquivos **por outra conta ou em outro computador** (backup copiado, disco removido). **Não** protege contra programas executados como o mesmo usuário: um malware na sua sessão pode chamar o DPAPI como o Storiva faz.
- Enquanto o Storiva está aberto e conectado, as credenciais estão na memória do processo (strings do .NET não podem ser zeradas de forma confiável).
- O DPAPI **não sincroniza** segredos entre computadores. Em outro PC, cadastre as credenciais de novo.
- Uma redefinição de senha feita por um administrador (não pelo próprio usuário) pode invalidar as chaves do DPAPI; os segredos ficam ilegíveis.
- O Storiva não é um gerenciador de senhas. Para chaves de longa duração, prefira credenciais temporárias (SSO, assume-role, STS).
- Com "Allow unencrypted HTTP", o segredo não trafega (a SigV4 envia só uma assinatura), mas os dados e os metadados trafegam sem cifragem.

## 5. Remover tudo

1. Em **Accounts › Manage Accounts..**, use **Remove** em cada conexão. Isso apaga a entrada de `connections.json` e o arquivo correspondente em `secrets\`. Nada é alterado no servidor.
2. Ou feche o Storiva e apague a pasta `%LOCALAPPDATA%\Storiva` inteira (PowerShell: `Remove-Item "$env:LOCALAPPDATA\Storiva" -Recurse`).
3. Se as credenciais foram expostas em algum lugar, por exemplo numa captura de tela, **revogue ou rotacione a chave** no console do provedor: apagar o arquivo local não invalida a chave.
