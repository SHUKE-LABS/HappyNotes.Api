# Database: HappyNotes Table: UserExternalLogin

 Field           | Type         | Null | Default | Comment
-----------------|--------------|------|---------|---------
 Id              | bigint       | NO   |         |
 UserId          | bigint       | NO   |         |
 Provider        | varchar(20)  | NO   |         |
 ProviderSubject | varchar(255) | NO   |         |
 CreatedAt       | bigint       | NO   |         |

## Indexes: 

 Key_name | Column_name     | Seq_in_index | Non_unique | Index_type | Visible
----------|-----------------|--------------|------------|------------|---------
 PRIMARY  | Id              |            1 |          0 | BTREE      | YES
 uniq     | Provider        |            1 |          0 | BTREE      | YES
 uniq     | ProviderSubject |            2 |          0 | BTREE      | YES
 UserId   | UserId          |            1 |          1 | BTREE      | YES
