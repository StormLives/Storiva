# Configuração de conexões

Os endereços e as chaves deste documento são **fictícios**. Não use as credenciais de outra pessoa nem as de uma conta de produção para experimentar.

## 1. Onde fica

- **Accounts › Add New Account..**: cadastra uma conexão. Ao salvar, o Storiva pergunta se deve conectar agora.
- **Accounts › Manage Accounts..**: lista as conexões, com provedor, credenciais, estado e data do último teste. Permite **Add / Edit / Test / Connect / Disconnect / Remove**.
- **Menu Accounts**: a lista no fim do menu mostra as conexões salvas (clique para conectar) e, conectado, **Disconnect from "…"**.
- **Sem conexão** (ao abrir o Storiva ou depois de desconectar): a lista mostra o painel **"Not connected"**, com um link *Connect to …* para cada conexão salva, *Add New Account..* e *Manage Accounts..*. Não há modo demo.

Nenhuma conexão é aberta automaticamente, nem ao iniciar o Storiva nem ao carregar ou copiar um `connections.json`. A rede só é usada depois de **Test** ou **Connect**.

## 2. AWS S3

| Campo | Valor |
| --- | --- |
| Account type | Amazon S3 (AWS) |
| Region | Região usada para assinar as requisições, por exemplo `eu-west-1`. Buckets em outras regiões são encontrados automaticamente (região do `ListBuckets`; em caso de redirecionamento, uma consulta `GetBucketLocation` e **uma** nova tentativa) |
| Credentials | Veja abaixo, em ordem de preferência |

### Métodos de autenticação

1. **Named AWS profile** (recomendado): o nome de um perfil dos arquivos `%USERPROFILE%\.aws\config` e `credentials`, inclusive perfis **SSO** (`aws sso login`) e **assume-role**. O Storiva guarda só o nome do perfil, e as credenciais temporárias continuam com o AWS SDK. O link *list profiles found on this computer* lê apenas os **nomes**.
2. **AWS default credential chain**: variáveis de ambiente, perfil `default`, SSO e funções de contêiner ou instância. É oferecida só para AWS (D-49). Atenção: fora de uma instância EC2, o SDK pode tentar o serviço de metadados local (169.254.169.254) antes de falhar.
3. **Access key ID + secret access key** (+ *session token* opcional, para credenciais temporárias do STS): o segredo é cifrado com DPAPI (ver [CREDENTIAL_SECURITY.md](CREDENTIAL_SECURITY.md)). Prefira chaves de um usuário IAM com permissões mínimas.

### Permissões IAM usadas

| Ação no Storiva | Permissão S3 |
| --- | --- |
| Test / Connect (lista de buckets) | `s3:ListAllMyBuckets` (sem ela: "Connected (limited permissions)") |
| Abrir bucket, listar | `s3:ListBucket` |
| Download, Preview | `s3:GetObject` |
| Properties, Headers | `s3:GetObject` (HEAD) |
| Upload, New Folder | `s3:PutObject` |
| Delete | `s3:DeleteObject` |
| Copy / Move / Rename / Storage class | `s3:GetObject` + `s3:PutObject` (+ `s3:DeleteObject` para mover) |
| Permissions | `s3:GetObjectAcl` / `s3:GetBucketAcl` |
| Tags | `s3:GetObjectTagging`, `s3:PutObjectTagging`, `s3:GetBucketTagging`, `s3:PutBucketTagging` |
| Versions | `s3:GetBucketVersioning`, `s3:ListBucketVersions`, `s3:PutBucketVersioning` |
| Create / Delete bucket | `s3:CreateBucket` / `s3:DeleteBucket` |
| Add External Bucket | `s3:ListBucket` no bucket (HEAD bucket) |

Uma credencial válida não garante permissão para tudo: cada operação recusada aparece como **Access denied**, com o código do servidor, e nada é dado como concluído.

## 3. MinIO e outros servidores compatíveis com S3

| Campo | Exemplo (fictício) | Observação |
| --- | --- | --- |
| Account type | S3-compatible server | |
| Endpoint URL | `https://minio.example.internal:9000` | Esquema **obrigatório**; sem usuário, caminho, query ou fragmento |
| Region | `us-east-1` | Região que o servidor espera na assinatura (padrão do MinIO: `us-east-1`; configurável no servidor com `MINIO_SITE_REGION`) |
| Credentials | Access key ou perfil AWS nomeado | A cadeia padrão AWS não é oferecida aqui |
| advanced settings.. › path-style | **ligado** por padrão | Necessário na maioria dos MinIO e em nomes de bucket com pontos |
| advanced settings.. › Allow unencrypted HTTP | desligado | Só para servidores de teste isolados. Sem ele, `http://` é recusado |
| advanced settings.. › flexible checksums | **desligado** por padrão | Ligue se o servidor aceitar os checksums CRC do SDK v4 (MinIO recente, AWS) |

### Path-style × virtual-hosted

- **Path-style**: `https://host:9000/bucket/chave`. Funciona com qualquer DNS.
- **Virtual-hosted**: `https://bucket.host/chave`. Exige DNS curinga (`*.host`) e certificado correspondente. É o padrão da AWS; use-o com servidores compatíveis só se estiverem configurados assim.

### Recursos que podem não existir no servidor

ACL, tags, versionamento e storage classes são extensões que nem todo servidor implementa. Quando falta suporte, a aba ou o comando mostra **Not supported by this server** (HTTP 501/405), e o resto continua funcionando. Para servidores compatíveis, o Storiva não oferece troca de storage class.

## 4. Testar a conexão

**Test connection** (no diálogo) e **Test** (em Manage Accounts) enviam apenas `ListBuckets`, uma requisição de leitura. Nada é criado, alterado ou excluído. Resultados possíveis:

| Resultado | Significado | O que fazer |
| --- | --- | --- |
| Connected | Credenciais aceitas; N buckets visíveis | — |
| Connected (limited permissions) | Assinatura aceita, mas a listagem de buckets foi negada | Use Buckets › Add External Bucket com o nome do bucket |
| Invalid credentials | `InvalidAccessKeyId`, `SignatureDoesNotMatch`, token expirado, relógio fora de sincronia | Confira a chave, o segredo, a validade do token e o relógio do Windows. Em servidores compatíveis, também a **região** |
| No credentials | Perfil inexistente, cadeia vazia, segredo não decifrável | Edite a conexão |
| Access denied | Política ou ACL nega a operação | Peça a permissão (tabela acima) |
| Network error | DNS, porta fechada, firewall, proxy, tempo esgotado | Confira o endereço e a porta |
| TLS error | Certificado não confiável, expirado ou com nome diferente | Ver a seção 5 |
| Region/endpoint mismatch | Região errada, endpoint ou endereçamento incompatível | Ajuste a região e o path-style |

As mensagens trazem o código do servidor e o HTTP status, nunca a chave nem o segredo.

## 5. Problemas de TLS (certificados)

O Storiva **sempre** valida certificados pelo repositório do Windows. Não existe opção para ignorar erros de certificado (D-51).

**Certificado emitido por uma autoridade certificadora (CA) privada:** instale o certificado da CA raiz como confiável no Windows.

1. Obtenha o certificado da CA (arquivo `.cer`/`.crt`) com o administrador do servidor e confira a impressão digital (*thumbprint*) por outro canal.
2. Para o seu usuário, sem administrador: `certmgr.msc` › *Autoridades de Certificação Raiz Confiáveis* › *Certificados* › botão direito › *Todas as Tarefas* › *Importar…*. Ou, no PowerShell: `Import-Certificate -FilePath .\ca-interna.cer -CertStoreLocation Cert:\CurrentUser\Root`.
3. Para todos os usuários (administrador): `certlm.msc`, ou `Import-Certificate -FilePath .\ca-interna.cer -CertStoreLocation Cert:\LocalMachine\Root`.
4. Reinicie o Storiva e teste a conexão de novo.

**Outras causas comuns:**

- o nome no certificado não corresponde ao host da URL (use o nome DNS que consta no certificado, não o IP);
- o certificado expirou;
- falta o certificado intermediário no servidor;
- um proxy de inspeção TLS da empresa usa uma CA que não está instalada.

**Certificado autoassinado no servidor:** prefira emitir um certificado por uma CA interna. Importar um autoassinado como raiz confiável faz o Windows confiar nele para **qualquer** finalidade; faça isso só em máquinas de teste.

## 6. Proxy

As conexões usam as configurações de proxy do Windows por meio do .NET. O Storiva não tem configuração própria de proxy.

## 7. O que ainda não é suportado

URLs pré-assinadas e "Generate Web URL", políticas de bucket, CORS, lifecycle, replicação, criptografia padrão, Object Lock, CloudFront, IAM, sincronização de pastas e "Mount as drive". Também não há edição de ACL ou de headers, restauração ou exclusão de versões, cópia de objetos maiores que 5 GB, limite de banda, importação ou exportação de conexões, nem tema de alto contraste.
